using System.Text;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using Microsoft.Extensions.Logging;

namespace AgentPulse.Infrastructure.Mutations;

internal sealed class WorkspaceMutationService(
    IProtectedPathPolicy protectedPathPolicy,
    IUnifiedDiffGenerator diffGenerator,
    IPathLockManager pathLockManager,
    IMutationFileSystem fileSystem,
    MutationToolOptions options,
    ILogger<WorkspaceMutationService> logger) : IWorkspaceMutationService
{
    public Task<AgentToolResult> ExecuteAsync(
        IAgentTool tool,
        System.Text.Json.JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.HasResourcePermissionAuthorizer)
        {
            logger.LogWarning(
                "Deferred permission runtime guard denied execution for tool {ToolName}.",
                tool.Name);
            return Task.FromResult(AgentToolResult.Failure(
                $"Deferred permission authorization is not configured for tool '{tool.Name}'. Execution was denied.",
                classification: AgentToolFailureClassification.Deterministic));
        }

        return tool.ExecuteAsync(arguments, context, cancellationToken);
    }

    public async Task<FileMutationPlan> PlanWriteAsync(
        string workspaceRoot,
        string path,
        string content,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        options.Validate();
        var target = protectedPathPolicy.ResolveAndValidate(workspaceRoot, path);
        if (fileSystem.DirectoryExists(target.FullPath))
        {
            throw new MutationValidationException("The target path is a directory, not a file.");
        }

        FileMutationOperation operation;
        if (fileSystem.FileExists(target.FullPath))
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                logger.LogInformation(
                    "Existing file overwrite rejected because expected SHA-256 was missing for target {Target}.",
                    target.RelativePath);
                throw new MutationValidationException(
                    "The target file already exists. Read it first and provide its expected SHA-256 value, or use the edit tool.");
            }

            ValidateSha256(expectedSha256);
            var before = await TextFileCodec.ReadAsync(
                target.FullPath,
                target.RelativePath,
                options.MaxFileBytes,
                cancellationToken);
            if (!string.Equals(before.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Existing file overwrite rejected because expected SHA-256 did not match for target {Target}.",
                    target.RelativePath);
                throw WritePreconditionMismatch();
            }

            logger.LogInformation(
                "Existing file overwrite precondition validated for target {Target}.",
                target.RelativePath);

            var afterBytes = TextFileCodec.EncodePreserving(before, content);
            ValidateFileSize(afterBytes.Length, target.RelativePath);
            var afterText = before.Encoding.Encoding.GetString(
                afterBytes,
                before.Encoding.Encoding.GetPreamble().Length,
                afterBytes.Length - before.Encoding.Encoding.GetPreamble().Length);
            var diff = diffGenerator.CreateUpdate(target.RelativePath, before.Text, afterText);
            operation = new FileMutationOperation(
                FileMutationKind.Update,
                target,
                null,
                before,
                afterText,
                afterBytes,
                TextFileCodec.ComputeSha256(afterBytes),
                diff,
                [new MutationPermissionTarget("write", target.RelativePath)]);
        }
        else
        {
            var afterBytes = TextFileCodec.EncodeNewFile(content);
            ValidateFileSize(afterBytes.Length, target.RelativePath);
            var diff = diffGenerator.CreateAdd(target.RelativePath, content);
            operation = new FileMutationOperation(
                FileMutationKind.Add,
                target,
                null,
                null,
                content,
                afterBytes,
                TextFileCodec.ComputeSha256(afterBytes),
                diff,
                [new MutationPermissionTarget("write", target.RelativePath)]);
        }

        logger.LogInformation(
            "Mutation plan created for operation {Operation} with {FileCount} file operation.",
            "write",
            1);
        return new FileMutationPlan(
            Path.GetFullPath(workspaceRoot),
            "write",
            [operation],
            operation.Diff);
    }

    public async Task<FileMutationPlan> PlanEditAsync(
        string workspaceRoot,
        string path,
        IReadOnlyList<TextReplacement> edits,
        bool multiEdit,
        CancellationToken cancellationToken)
    {
        options.Validate();
        if (edits.Count == 0)
        {
            throw new MutationValidationException(
                multiEdit ? "The multi_edit tool requires at least one edit." : "The edit tool requires one edit.");
        }

        var target = protectedPathPolicy.ResolveAndValidate(workspaceRoot, path);
        if (!fileSystem.FileExists(target.FullPath))
        {
            throw new MutationValidationException(
                fileSystem.DirectoryExists(target.FullPath)
                    ? "The target path is a directory, not a file."
                    : "The target file does not exist.");
        }

        var before = await TextFileCodec.ReadAsync(
            target.FullPath,
            target.RelativePath,
            options.MaxFileBytes,
            cancellationToken);
        var staged = before.Text;
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            try
            {
                staged = ApplyReplacement(staged, before.LineEnding, edit);
            }
            catch (MutationValidationException exception) when (multiEdit)
            {
                throw new MutationValidationException(
                    $"Multi-edit operation {index + 1} failed because {LowercaseFirst(exception.Message)} No changes were written.");
            }
        }

        var afterBytes = TextFileCodec.EncodePreserving(
            before,
            staged,
            normalizeLineEndings: false);
        if (TextFileCodec.HasMixedLineEndings(before.Text))
        {
            logger.LogInformation(
                "Mixed line-ending file preserved for mutation target {Path}.",
                target.RelativePath);
        }
        ValidateFileSize(afterBytes.Length, target.RelativePath);
        var normalizedText = before.Encoding.Encoding.GetString(
            afterBytes,
            before.Encoding.Encoding.GetPreamble().Length,
            afterBytes.Length - before.Encoding.Encoding.GetPreamble().Length);
        var diff = diffGenerator.CreateUpdate(target.RelativePath, before.Text, normalizedText);
        var operation = new FileMutationOperation(
            FileMutationKind.Update,
            target,
            null,
            before,
            normalizedText,
            afterBytes,
            TextFileCodec.ComputeSha256(afterBytes),
            diff,
            [new MutationPermissionTarget("edit", target.RelativePath)]);
        var toolName = multiEdit ? "multi_edit" : "edit";
        logger.LogInformation(
            "Mutation plan created for operation {Operation} with {FileCount} file operation.",
            toolName,
            1);
        return new FileMutationPlan(
            Path.GetFullPath(workspaceRoot),
            toolName,
            [operation],
            diff);
    }

    public async Task<FileMutationPlan> PlanPatchAsync(
        string workspaceRoot,
        PatchDocument patch,
        CancellationToken cancellationToken)
    {
        options.Validate();
        ArgumentNullException.ThrowIfNull(patch);
        var resolved = new List<(PatchCommand Command, ResolvedMutationPath Source, ResolvedMutationPath? Destination)>();
        var fullPaths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var command in patch.Commands)
        {
            var source = protectedPathPolicy.ResolveAndValidate(workspaceRoot, command.Path);
            if (!fullPaths.Add(source.FullPath))
            {
                throw new MutationValidationException(
                    $"The patch contains conflicting operations for '{source.RelativePath}'.");
            }

            ResolvedMutationPath? destination = null;
            if (command.MoveTo is not null)
            {
                destination = protectedPathPolicy.ResolveAndValidate(workspaceRoot, command.MoveTo);
                if (!fullPaths.Add(destination.FullPath))
                {
                    throw new MutationValidationException(
                        $"The patch contains conflicting operations for '{destination.RelativePath}'.");
                }
            }

            resolved.Add((command, source, destination));
        }

        var operations = new List<FileMutationOperation>(resolved.Count);
        foreach (var item in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (item.Command.Kind)
            {
                case PatchCommandKind.Add:
                    if (fileSystem.FileExists(item.Source.FullPath) ||
                        fileSystem.DirectoryExists(item.Source.FullPath))
                    {
                        throw new MutationValidationException(
                            $"The patch cannot add '{item.Source.RelativePath}' because it already exists.");
                    }

                    var addText = item.Command.AddedLines.Count == 0
                        ? string.Empty
                        : string.Join('\n', item.Command.AddedLines) + "\n";
                    var addBytes = TextFileCodec.EncodeNewFile(addText);
                    ValidateFileSize(addBytes.Length, item.Source.RelativePath);
                    operations.Add(new FileMutationOperation(
                        FileMutationKind.Add,
                        item.Source,
                        null,
                        null,
                        addText,
                        addBytes,
                        TextFileCodec.ComputeSha256(addBytes),
                        diffGenerator.CreateAdd(item.Source.RelativePath, addText),
                        [new MutationPermissionTarget("write", item.Source.RelativePath)]));
                    break;

                case PatchCommandKind.Delete:
                    if (fileSystem.DirectoryExists(item.Source.FullPath))
                    {
                        throw new MutationValidationException(
                            $"The patch cannot delete directory '{item.Source.RelativePath}'.");
                    }

                    if (!fileSystem.FileExists(item.Source.FullPath))
                    {
                        throw new MutationValidationException(
                            $"The patch cannot delete missing file '{item.Source.RelativePath}'.");
                    }

                    var deleted = await TextFileCodec.ReadAsync(
                        item.Source.FullPath,
                        item.Source.RelativePath,
                        options.MaxFileBytes,
                        cancellationToken);
                    operations.Add(new FileMutationOperation(
                        FileMutationKind.Delete,
                        item.Source,
                        null,
                        deleted,
                        null,
                        null,
                        string.Empty,
                        diffGenerator.CreateDelete(item.Source.RelativePath, deleted.Text),
                        [new MutationPermissionTarget("delete", item.Source.RelativePath)]));
                    break;

                case PatchCommandKind.Update:
                    if (!fileSystem.FileExists(item.Source.FullPath))
                    {
                        throw new MutationValidationException(
                            fileSystem.DirectoryExists(item.Source.FullPath)
                                ? $"The patch cannot update directory '{item.Source.RelativePath}'."
                                : $"The patch cannot update missing file '{item.Source.RelativePath}'.");
                    }

                    if (item.Destination is not null &&
                        (fileSystem.FileExists(item.Destination.FullPath) ||
                         fileSystem.DirectoryExists(item.Destination.FullPath)))
                    {
                        throw new MutationValidationException(
                            $"The patch cannot move to '{item.Destination.RelativePath}' because the destination already exists.");
                    }

                    var before = await TextFileCodec.ReadAsync(
                        item.Source.FullPath,
                        item.Source.RelativePath,
                        options.MaxFileBytes,
                        cancellationToken);
                    var updatedText = ApplyHunks(before, item.Command.Hunks);
                    var updatedBytes = item.Command.Hunks.Count == 0
                        ? before.Bytes.ToArray()
                        : TextFileCodec.EncodePreserving(before, updatedText, normalizeLineEndings: false);
                    if (item.Command.Hunks.Count > 0 && TextFileCodec.HasMixedLineEndings(before.Text))
                    {
                        logger.LogInformation(
                            "Mixed line-ending file preserved for mutation target {Path}.",
                            item.Source.RelativePath);
                    }
                    ValidateFileSize(
                        updatedBytes.Length,
                        item.Destination?.RelativePath ?? item.Source.RelativePath);
                    var decodedUpdatedText = before.Encoding.Encoding.GetString(
                        updatedBytes,
                        before.Encoding.Encoding.GetPreamble().Length,
                        updatedBytes.Length - before.Encoding.Encoding.GetPreamble().Length);
                    if (item.Destination is null)
                    {
                        operations.Add(new FileMutationOperation(
                            FileMutationKind.Update,
                            item.Source,
                            null,
                            before,
                            decodedUpdatedText,
                            updatedBytes,
                            TextFileCodec.ComputeSha256(updatedBytes),
                            diffGenerator.CreateUpdate(
                                item.Source.RelativePath,
                                before.Text,
                                decodedUpdatedText),
                            [new MutationPermissionTarget("edit", item.Source.RelativePath)]));
                    }
                    else
                    {
                        operations.Add(new FileMutationOperation(
                            FileMutationKind.Move,
                            item.Source,
                            item.Destination,
                            before,
                            decodedUpdatedText,
                            updatedBytes,
                            TextFileCodec.ComputeSha256(updatedBytes),
                            diffGenerator.CreateMove(
                                item.Source.RelativePath,
                                item.Destination.RelativePath,
                                before.Text,
                                decodedUpdatedText),
                            [
                                new MutationPermissionTarget("move", item.Source.RelativePath),
                                new MutationPermissionTarget("move", item.Destination.RelativePath),
                            ]));
                    }

                    break;

                default:
                    throw new MutationValidationException("The patch contains an unsupported operation.");
            }
        }

        var combined = diffGenerator.Combine(operations.Select(static operation => operation.Diff));
        logger.LogInformation(
            "Mutation plan created for operation {Operation} with {FileCount} file operations.",
            "apply_patch",
            operations.Count);
        return new FileMutationPlan(
            Path.GetFullPath(workspaceRoot),
            "apply_patch",
            operations,
            combined);
    }

    public async Task<FileMutationResult> AuthorizeAndCommitAsync(
        FileMutationPlan plan,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        if (!PathsEqual(plan.WorkspaceRoot, context.WorkspaceRoot))
        {
            throw new MutationValidationException(
                "The mutation plan does not belong to the active workspace.");
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var permissionTargets = plan.Operations
            .SelectMany(static operation => operation.PermissionTargets)
            .DistinctBy(
                static target => $"{target.Operation}\u001F{target.RelativePath}",
                comparer)
            .OrderBy(static target => target.RelativePath, comparer)
            .ThenBy(static target => target.Operation, StringComparer.Ordinal)
            .ToArray();
        var description = $"Proposed changes:{Environment.NewLine}{plan.Diff.Preview}";
        foreach (var target in permissionTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Permission requested for mutation operation {Operation} and target {Target}.",
                target.Operation,
                target.RelativePath);
            var authorization = await context.AuthorizeResourceAsync(
                target.Operation,
                target.RelativePath,
                description,
                cancellationToken);
            if (!authorization.IsAllowed)
            {
                throw new MutationPermissionException(ClassifyPermissionFailure(
                    authorization,
                    plan.ToolOperation,
                    target.RelativePath));
            }
        }

        var lockPaths = GetLockPaths(plan);
        await using var pathLocks = await pathLockManager.AcquireAsync(lockPaths, cancellationToken);
        await RevalidateAsync(plan, cancellationToken);

        var createdDirectories = new List<string>();
        var staged = new List<StagedMutation>(plan.Operations.Count);
        try
        {
            foreach (var operation in plan.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                staged.Add(await StageAsync(operation, cancellationToken));
            }

            logger.LogInformation(
                "Mutation staging completed for {FileCount} file operations.",
                staged.Count);
            await RevalidateAsync(plan, cancellationToken);
            logger.LogInformation(
                "Atomic commit started for {FileCount} file operations.",
                staged.Count);
            try
            {
                CreateParentDirectories(plan, createdDirectories);
                await RevalidateAsync(plan, cancellationToken);
                foreach (var value in staged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Commit(value);
                }

                await VerifyFinalStateAsync(plan, cancellationToken);
                CleanupBackups(staged);
                var temporaryCleanupFailures = CleanupTemporaryFiles(staged);
                if (temporaryCleanupFailures.Count > 0)
                {
                    throw new IOException(
                        "Atomic mutation committed, but temporary file cleanup failed.",
                        new AggregateException(temporaryCleanupFailures));
                }
            }
            catch (Exception commitException)
            {
                await RollbackAsync(staged, commitException);
                throw;
            }

            logger.LogInformation(
                "Atomic commit completed for {FileCount} file operations.",
                staged.Count);
        }
        catch (Exception exception)
        {
            var cleanupFailures = CleanupAfterFailure(staged, createdDirectories);
            if (cleanupFailures.Count > 0)
            {
                throw new MutationRollbackException(
                    "Mutation failed and temporary artifact cleanup did not complete.",
                    new AggregateException(new[] { exception }.Concat(cleanupFailures)));
            }

            throw;
        }

        return CreateResult(plan);
    }

    private static IReadOnlyList<string> GetLockPaths(FileMutationPlan plan)
    {
        var paths = new List<string>();
        foreach (var operation in plan.Operations)
        {
            paths.Add(operation.Source.FullPath);
            if (operation.Destination is not null)
            {
                paths.Add(operation.Destination.FullPath);
            }

            if (operation.Kind is not FileMutationKind.Add and not FileMutationKind.Move)
            {
                continue;
            }

            var target = operation.Destination?.FullPath ?? operation.Source.FullPath;
            var parent = Path.GetDirectoryName(target);
            while (!string.IsNullOrEmpty(parent) &&
                   IsInside(plan.WorkspaceRoot, parent) &&
                   !PathsEqual(plan.WorkspaceRoot, parent))
            {
                paths.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        return paths;
    }

    private async Task RevalidateAsync(FileMutationPlan plan, CancellationToken cancellationToken)
    {
        RevalidateCanonicalPaths(plan);
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (operation.Kind)
            {
                case FileMutationKind.Add:
                    if (fileSystem.FileExists(operation.Source.FullPath) ||
                        fileSystem.DirectoryExists(operation.Source.FullPath))
                    {
                        logger.LogWarning(
                            "Concurrency validation failed for mutation target {Target}.",
                            operation.Source.RelativePath);
                        throw StaleFile();
                    }

                    break;
                case FileMutationKind.Update:
                case FileMutationKind.Delete:
                case FileMutationKind.Move:
                    await ValidateExistingSnapshotAsync(operation, cancellationToken);
                    if (operation.Kind == FileMutationKind.Move && operation.Destination is not null &&
                        (fileSystem.FileExists(operation.Destination.FullPath) ||
                         fileSystem.DirectoryExists(operation.Destination.FullPath)))
                    {
                        logger.LogWarning(
                            "Concurrency validation failed for mutation destination {Target}.",
                            operation.Destination.RelativePath);
                        throw StaleFile();
                    }

                    break;
                default:
                    throw new InvalidOperationException("Mutation operation kind is invalid.");
            }
        }
    }

    private async Task ValidateExistingSnapshotAsync(
        FileMutationOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation.Before is null || !fileSystem.FileExists(operation.Source.FullPath))
        {
            logger.LogWarning(
                "Concurrency validation failed for mutation target {Target}.",
                operation.Source.RelativePath);
            throw StaleFile();
        }

        var info = new FileInfo(operation.Source.FullPath);
        if (info.Length > options.MaxFileBytes)
        {
            logger.LogWarning(
                "Concurrency validation failed for mutation target {Target} because it exceeded the configured size limit.",
                operation.Source.RelativePath);
            throw StaleFile();
        }

        var bytes = await File.ReadAllBytesAsync(operation.Source.FullPath, cancellationToken);
        if (bytes.LongLength > options.MaxFileBytes || !string.Equals(
                TextFileCodec.ComputeSha256(bytes),
                operation.Before.Sha256,
                StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Concurrency validation failed for mutation target {Target}.",
                operation.Source.RelativePath);
            throw StaleFile();
        }
    }

    private void RevalidateCanonicalPaths(FileMutationPlan plan)
    {
        foreach (var operation in plan.Operations)
        {
            RevalidateCanonicalPath(plan.WorkspaceRoot, operation.Source);
            if (operation.Destination is not null)
            {
                RevalidateCanonicalPath(plan.WorkspaceRoot, operation.Destination);
            }
        }
    }

    private void RevalidateCanonicalPath(string workspaceRoot, ResolvedMutationPath planned)
    {
        var current = protectedPathPolicy.ResolveAndValidate(workspaceRoot, planned.RelativePath);
        if (!PathsEqual(current.FullPath, planned.FullPath))
        {
            logger.LogWarning(
                "Concurrency validation failed for mutation target {Target}.",
                planned.RelativePath);
            throw StaleFile();
        }
    }

    private void CreateParentDirectories(FileMutationPlan plan, List<string> createdDirectories)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var required = plan.Operations
            .Where(static operation => operation.Kind is FileMutationKind.Add or FileMutationKind.Move)
            .Select(operation => Path.GetDirectoryName(
                operation.Destination?.FullPath ?? operation.Source.FullPath))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(comparer)
            .OrderBy(static path => path.Length)
            .ThenBy(static path => path, comparer);
        foreach (var directory in required)
        {
            var missing = new Stack<string>();
            var current = directory;
            while (!fileSystem.DirectoryExists(current))
            {
                if (!IsInside(plan.WorkspaceRoot, current))
                {
                    throw new MutationValidationException(
                        "A mutation parent directory resolves outside the active workspace.");
                }

                missing.Push(current);
                current = Path.GetDirectoryName(current) ?? plan.WorkspaceRoot;
            }

            while (missing.Count > 0)
            {
                var path = missing.Pop();
                fileSystem.CreateDirectory(path);
                createdDirectories.Add(path);
            }
        }
    }

    private async Task<StagedMutation> StageAsync(
        FileMutationOperation operation,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            if (operation.AfterBytes is not null)
            {
                var target = operation.Destination?.FullPath ?? operation.Source.FullPath;
                var directory = FindExistingStagingDirectory(target);
                temporaryPath = CreateArtifactPath(directory, ".tmp");
                await fileSystem.WriteStagedFileAsync(
                    temporaryPath,
                    operation.AfterBytes,
                    operation.Before?.UnixMode,
                    cancellationToken);
            }

            var backupDirectory = Path.GetDirectoryName(operation.Source.FullPath)
                ?? throw new InvalidOperationException("A mutation source must have a parent directory.");
            var backupPath = operation.Kind == FileMutationKind.Add
                ? null
                : CreateArtifactPath(backupDirectory, ".bak");
            return new StagedMutation(operation, temporaryPath, backupPath);
        }
        catch (Exception stagingException)
        {
            if (temporaryPath is not null && fileSystem.FileExists(temporaryPath))
            {
                try
                {
                    fileSystem.DeleteFile(temporaryPath);
                }
                catch (Exception cleanupException)
                {
                    throw new IOException(
                        "Mutation staging failed and temporary file cleanup also failed.",
                        new AggregateException(stagingException, cleanupException));
                }
            }

            throw;
        }
    }

    private string FindExistingStagingDirectory(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException(
            "A mutation target must have a parent directory.");
        while (!fileSystem.DirectoryExists(directory))
        {
            directory = Path.GetDirectoryName(directory) ?? throw new MutationValidationException(
                "A mutation target does not have an existing workspace ancestor for staging.");
        }

        return directory;
    }

    private void Commit(StagedMutation staged)
    {
        staged.CommitStarted = true;
        switch (staged.Operation.Kind)
        {
            case FileMutationKind.Add:
                fileSystem.MoveFile(
                    staged.TemporaryPath!,
                    staged.Operation.Source.FullPath);
                staged.TargetInstalled = true;
                break;
            case FileMutationKind.Update:
                fileSystem.ReplaceFile(
                    staged.TemporaryPath!,
                    staged.Operation.Source.FullPath,
                    staged.BackupPath!);
                staged.TargetInstalled = true;
                PreserveMetadata(staged.Operation, staged.Operation.Source.FullPath);
                break;
            case FileMutationKind.Delete:
                fileSystem.MoveFile(
                    staged.Operation.Source.FullPath,
                    staged.BackupPath!);
                staged.SourceBackedUp = true;
                break;
            case FileMutationKind.Move:
                fileSystem.MoveFile(
                    staged.Operation.Source.FullPath,
                    staged.BackupPath!);
                staged.SourceBackedUp = true;
                fileSystem.MoveFile(
                    staged.TemporaryPath!,
                    staged.Operation.Destination!.FullPath);
                staged.TargetInstalled = true;
                PreserveMetadata(staged.Operation, staged.Operation.Destination.FullPath);
                break;
            default:
                throw new InvalidOperationException("Mutation operation kind is invalid.");
        }
    }

    private void PreserveMetadata(FileMutationOperation operation, string targetPath)
    {
        if (operation.Before is null)
        {
            return;
        }

        fileSystem.SetAttributes(targetPath, operation.Before.Attributes);
        if (operation.Before.UnixMode is { } mode && !OperatingSystem.IsWindows())
        {
            fileSystem.SetUnixFileMode(targetPath, mode);
        }
    }

    private static async Task VerifyFinalStateAsync(
        FileMutationPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.Kind == FileMutationKind.Delete)
            {
                if (File.Exists(operation.Source.FullPath))
                {
                    throw new IOException(
                        $"Atomic mutation verification failed for '{operation.Source.RelativePath}'.");
                }

                continue;
            }

            var target = operation.Destination ?? operation.Source;
            if (!File.Exists(target.FullPath))
            {
                throw new IOException(
                    $"Atomic mutation verification failed for '{target.RelativePath}'.");
            }

            var bytes = await File.ReadAllBytesAsync(target.FullPath, cancellationToken);
            if (!string.Equals(
                    TextFileCodec.ComputeSha256(bytes),
                    operation.AfterSha256,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Atomic mutation verification failed for '{target.RelativePath}'.");
            }

            if (operation.Kind == FileMutationKind.Move && File.Exists(operation.Source.FullPath))
            {
                throw new IOException(
                    $"Atomic move verification failed for '{operation.Source.RelativePath}'.");
            }
        }
    }

    private async Task RollbackAsync(
        IReadOnlyList<StagedMutation> staged,
        Exception originalException)
    {
        logger.LogWarning(
            "Mutation rollback started after an atomic commit failure of type {FailureType}.",
            originalException.GetType().Name);
        var failures = new List<Exception>();
        for (var index = staged.Count - 1; index >= 0; index--)
        {
            var value = staged[index];
            if (!value.CommitStarted)
            {
                continue;
            }

            try
            {
                await RollbackAsync(value);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                logger.LogError(
                    "Rollback failed for mutation target {Target} with failure type {FailureType}.",
                    value.Operation.Source.RelativePath,
                    exception.GetType().Name);
            }
        }

        if (failures.Count == 0)
        {
            logger.LogWarning("Mutation rollback completed successfully.");
            return;
        }

        var aggregate = new AggregateException(new[] { originalException }.Concat(failures));
        throw new MutationRollbackException(
            "Mutation failed and rollback did not complete. Review the structured logs before retrying.",
            aggregate);
    }

    private async Task RollbackAsync(StagedMutation staged)
    {
        var operation = staged.Operation;
        switch (operation.Kind)
        {
            case FileMutationKind.Add:
                if (staged.TargetInstalled)
                {
                    DeleteInstalledTargetIfOwned(operation.Source.FullPath, operation.AfterSha256);
                }

                break;
            case FileMutationKind.Update:
            case FileMutationKind.Delete:
                await RestoreOriginalOrVerifyAsync(staged);
                break;
            case FileMutationKind.Move:
                if (staged.TargetInstalled && operation.Destination is not null)
                {
                    DeleteInstalledTargetIfOwned(
                        operation.Destination.FullPath,
                        operation.AfterSha256);
                }

                await RestoreOriginalOrVerifyAsync(staged);
                break;
            default:
                throw new InvalidOperationException("Mutation operation kind is invalid.");
        }
    }

    private async Task RestoreOriginalOrVerifyAsync(StagedMutation staged)
    {
        var operation = staged.Operation;
        if (operation.Before is null)
        {
            throw new IOException("Rollback is missing the original file snapshot.");
        }

        if (staged.BackupPath is not null && fileSystem.FileExists(staged.BackupPath))
        {
            if (fileSystem.FileExists(operation.Source.FullPath))
            {
                if (operation.Kind == FileMutationKind.Update)
                {
                    DeleteInstalledTargetIfOwned(
                        operation.Source.FullPath,
                        operation.AfterSha256);
                }
                else
                {
                    throw new IOException(
                        $"Rollback could not restore '{operation.Source.RelativePath}' because the source path was recreated after commit.");
                }
            }

            fileSystem.MoveFile(staged.BackupPath, operation.Source.FullPath);
            return;
        }

        var sourceExists = fileSystem.FileExists(operation.Source.FullPath);
        if (sourceExists)
        {
            var bytes = File.ReadAllBytes(operation.Source.FullPath);
            var hash = TextFileCodec.ComputeSha256(bytes);
            if (string.Equals(hash, operation.Before.Sha256, StringComparison.Ordinal))
            {
                return;
            }

            if (operation.Kind != FileMutationKind.Update ||
                !staged.TargetInstalled ||
                !string.Equals(hash, operation.AfterSha256, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Rollback could not verify the current state of '{operation.Source.RelativePath}'.");
            }
        }
        else if (operation.Kind == FileMutationKind.Update || !staged.SourceBackedUp)
        {
            throw new IOException(
                $"Rollback could not restore '{operation.Source.RelativePath}' because its backup is missing.");
        }

        await RestoreSnapshotAsync(operation, replaceCurrent: sourceExists);
    }

    private async Task RestoreSnapshotAsync(
        FileMutationOperation operation,
        bool replaceCurrent)
    {
        var before = operation.Before ?? throw new IOException(
            "Rollback is missing the original file snapshot.");
        var directory = Path.GetDirectoryName(operation.Source.FullPath)
            ?? throw new IOException("Rollback target has no parent directory.");
        var temporaryPath = CreateArtifactPath(directory, ".tmp");
        string? replacementBackup = null;
        try
        {
            await fileSystem.WriteStagedFileAsync(
                temporaryPath,
                before.Bytes,
                before.UnixMode,
                CancellationToken.None);
            if (replaceCurrent)
            {
                replacementBackup = CreateArtifactPath(directory, ".bak");
                fileSystem.ReplaceFile(
                    temporaryPath,
                    operation.Source.FullPath,
                    replacementBackup);
            }
            else
            {
                fileSystem.MoveFile(temporaryPath, operation.Source.FullPath);
            }

            fileSystem.SetAttributes(operation.Source.FullPath, before.Attributes);
            if (before.UnixMode is { } mode && !OperatingSystem.IsWindows())
            {
                fileSystem.SetUnixFileMode(operation.Source.FullPath, mode);
            }
        }
        finally
        {
            var failures = new List<Exception>();
            foreach (var artifact in new[] { temporaryPath, replacementBackup })
            {
                if (artifact is null || !fileSystem.FileExists(artifact))
                {
                    continue;
                }

                try
                {
                    fileSystem.DeleteFile(artifact);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count > 0)
            {
                throw new IOException(
                    "Rollback restored the original file but could not remove all rollback artifacts.",
                    new AggregateException(failures));
            }
        }
    }

    private void DeleteInstalledTargetIfOwned(string path, string expectedSha256)
    {
        if (!fileSystem.FileExists(path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        if (!string.Equals(
                TextFileCodec.ComputeSha256(bytes),
                expectedSha256,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "Rollback could not remove a mutation target because it changed after commit.");
        }

        fileSystem.DeleteFile(path);
    }

    private void CleanupBackups(IEnumerable<StagedMutation> staged)
    {
        var failures = new List<Exception>();
        foreach (var value in staged)
        {
            if (value.BackupPath is null || !fileSystem.FileExists(value.BackupPath))
            {
                continue;
            }

            try
            {
                fileSystem.DeleteFile(value.BackupPath);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                logger.LogError(
                    "Backup file cleanup failed after mutation commit with failure type {FailureType}.",
                    exception.GetType().Name);
            }
        }

        if (failures.Count > 0)
        {
            throw new IOException(
                "Atomic mutation committed, but backup cleanup failed.",
                new AggregateException(failures));
        }
    }

    private IReadOnlyList<Exception> CleanupAfterFailure(
        IReadOnlyList<StagedMutation> staged,
        IReadOnlyList<string> createdDirectories)
    {
        var failures = new List<Exception>();
        failures.AddRange(CleanupTemporaryFiles(staged));
        failures.AddRange(RemoveCreatedDirectories(createdDirectories));
        return failures;
    }

    private IReadOnlyList<Exception> CleanupTemporaryFiles(IEnumerable<StagedMutation> staged)
    {
        var failures = new List<Exception>();
        foreach (var value in staged)
        {
            if (value.TemporaryPath is null || !fileSystem.FileExists(value.TemporaryPath))
            {
                continue;
            }

            try
            {
                fileSystem.DeleteFile(value.TemporaryPath);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                logger.LogError(
                    "Temp file cleanup failed after mutation operation with failure type {FailureType}.",
                    exception.GetType().Name);
            }
        }

        return failures;
    }

    private IReadOnlyList<Exception> RemoveCreatedDirectories(IEnumerable<string> directories)
    {
        var failures = new List<Exception>();
        foreach (var directory in directories.Reverse())
        {
            try
            {
                if (fileSystem.DirectoryExists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    fileSystem.DeleteDirectory(directory);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                logger.LogError(
                    "Temporary mutation directory cleanup failed with failure type {FailureType}.",
                    exception.GetType().Name);
            }
        }

        return failures;
    }

    private static string ApplyReplacement(
        string content,
        string lineEnding,
        TextReplacement edit)
    {
        if (string.IsNullOrEmpty(edit.OldText))
        {
            throw new MutationValidationException("the expected text must not be empty.");
        }

        if (string.Equals(
                TextFileCodec.NormalizeForDiff(edit.OldText),
                TextFileCodec.NormalizeForDiff(edit.NewText),
                StringComparison.Ordinal))
        {
            throw new MutationValidationException("the old and new text must be different.");
        }

        var matches = FindExactReplacementMatches(content, edit.OldText);
        if (matches.Count == 0)
        {
            matches = FindLogicalReplacementMatches(content, edit.OldText);
        }

        if (matches.Count == 0)
        {
            throw new MutationValidationException("the expected text was not found.");
        }

        if (!edit.ReplaceAll && matches.Count > 1)
        {
            throw new MutationValidationException(
                "the edit text matched more than once. Provide more surrounding context or set replace_all to true.");
        }

        IReadOnlyList<TextMatch> selected = edit.ReplaceAll ? matches : [matches[0]];
        var builder = new StringBuilder(content);
        foreach (var match in selected.OrderByDescending(static value => value.Start))
        {
            var replacement = AdaptReplacementLineEndings(
                edit.NewText,
                content,
                match.Start,
                match.Length,
                lineEnding);
            builder.Remove(match.Start, match.Length);
            builder.Insert(match.Start, replacement);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<TextMatch> FindExactReplacementMatches(
        string content,
        string value)
    {
        return FindMatches(content, value)
            .Select(index => new TextMatch(index, value.Length))
            .ToArray();
    }

    private static IReadOnlyList<TextMatch> FindLogicalReplacementMatches(
        string content,
        string value)
    {
        var normalizedValue = TextFileCodec.NormalizeForDiff(value);
        if (!normalizedValue.Contains('\n'))
        {
            return [];
        }

        var logical = CreateLogicalTextMap(content);
        return FindMatches(logical.Text, normalizedValue)
            .Select(index => new TextMatch(
                logical.Boundaries[index],
                logical.Boundaries[index + normalizedValue.Length] - logical.Boundaries[index]))
            .ToArray();
    }

    private static LogicalTextMap CreateLogicalTextMap(string content)
    {
        var builder = new StringBuilder(content.Length);
        var boundaries = new List<int>(content.Length + 1) { 0 };
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r')
            {
                if (index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }

                builder.Append('\n');
                boundaries.Add(index + 1);
            }
            else
            {
                builder.Append(content[index]);
                boundaries.Add(index + 1);
            }
        }

        return new LogicalTextMap(builder.ToString(), boundaries);
    }

    private static string AdaptReplacementLineEndings(
        string replacement,
        string content,
        int matchStart,
        int matchLength,
        string fallbackLineEnding)
    {
        if (!replacement.Contains('\r') && !replacement.Contains('\n'))
        {
            return replacement;
        }

        var matchedText = content.Substring(matchStart, matchLength);
        var matchedTerminators = TextFileCodec.ParseLineSegments(matchedText)
            .Select(static segment => segment.Terminator)
            .Where(static terminator => terminator.Length > 0)
            .ToArray();
        var contextualFallback = matchedTerminators.FirstOrDefault()
            ?? FindPreviousLineTerminator(content, matchStart)
            ?? FindNextLineTerminator(content, matchStart + matchLength)
            ?? fallbackLineEnding;

        var builder = new StringBuilder(replacement.Length);
        var terminatorIndex = 0;
        for (var index = 0; index < replacement.Length; index++)
        {
            if (replacement[index] != '\r' && replacement[index] != '\n')
            {
                builder.Append(replacement[index]);
                continue;
            }

            if (replacement[index] == '\r' &&
                index + 1 < replacement.Length &&
                replacement[index + 1] == '\n')
            {
                index++;
            }

            var terminator = terminatorIndex < matchedTerminators.Length
                ? matchedTerminators[terminatorIndex]
                : contextualFallback;
            builder.Append(terminator);
            terminatorIndex++;
        }

        return builder.ToString();
    }

    private static string? FindPreviousLineTerminator(string content, int start)
    {
        for (var index = Math.Min(start, content.Length) - 1; index >= 0; index--)
        {
            if (content[index] == '\n')
            {
                return index > 0 && content[index - 1] == '\r' ? "\r\n" : "\n";
            }

            if (content[index] == '\r')
            {
                return "\r";
            }
        }

        return null;
    }

    private static string? FindNextLineTerminator(string content, int start)
    {
        for (var index = Math.Max(0, start); index < content.Length; index++)
        {
            if (content[index] == '\r')
            {
                return index + 1 < content.Length && content[index + 1] == '\n'
                    ? "\r\n"
                    : "\r";
            }

            if (content[index] == '\n')
            {
                return "\n";
            }
        }

        return null;
    }

    private static IReadOnlyList<int> FindMatches(string content, string value)
    {
        var matches = new List<int>();
        var start = 0;
        while (start <= content.Length - value.Length)
        {
            var index = content.IndexOf(value, start, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            matches.Add(index);
            start = index + value.Length;
        }

        return matches;
    }

    private static string ApplyHunks(TextFileSnapshot before, IReadOnlyList<PatchHunk> hunks)
    {
        if (hunks.Count == 0)
        {
            return before.Text;
        }

        var segments = TextFileCodec.ParseLineSegments(before.Text);
        var minimumIndex = 0;
        foreach (var hunk in hunks)
        {
            var oldLines = hunk.Lines
                .Where(static line => line.Kind != PatchLineKind.Add)
                .Select(static line => line.Text)
                .ToArray();
            int matchIndex;
            if (oldLines.Length == 0)
            {
                matchIndex = segments.Count;
            }
            else
            {
                var matches = FindSequenceMatches(
                    segments.Select(static segment => segment.Content).ToArray(),
                    oldLines,
                    minimumIndex);
                if (matches.Count == 0)
                {
                    throw new MutationValidationException(
                        $"A patch hunk for '{before.RelativePath}' did not match the current file contents.");
                }

                if (matches.Count > 1)
                {
                    throw new MutationValidationException(
                        $"A patch hunk for '{before.RelativePath}' matched more than once.");
                }

                matchIndex = matches[0];
            }

            var source = segments.Skip(matchIndex).Take(oldLines.Length).ToArray();
            var replacement = BuildHunkReplacement(
                hunk,
                source,
                segments,
                matchIndex,
                before.LineEnding);
            segments.RemoveRange(matchIndex, oldLines.Length);
            segments.InsertRange(matchIndex, replacement);
            EnsureLineSeparationAtInsertionBoundary(segments, matchIndex, replacement.Count, before.LineEnding);
            minimumIndex = matchIndex + replacement.Count;
        }

        PreserveTrailingNewline(segments, before.HasTrailingNewline, before.LineEnding);
        return TextFileCodec.RebuildLineSegments(segments);
    }

    private static List<TextLineSegment> BuildHunkReplacement(
        PatchHunk hunk,
        IReadOnlyList<TextLineSegment> source,
        IReadOnlyList<TextLineSegment> allSegments,
        int matchIndex,
        string fallbackLineEnding)
    {
        var replacement = new List<TextLineSegment>();
        var removedTerminators = new Queue<string>();
        var sourceIndex = 0;
        foreach (var line in hunk.Lines)
        {
            switch (line.Kind)
            {
                case PatchLineKind.Context:
                    replacement.Add(source[sourceIndex]);
                    sourceIndex++;
                    break;
                case PatchLineKind.Remove:
                    removedTerminators.Enqueue(source[sourceIndex].Terminator);
                    sourceIndex++;
                    break;
                case PatchLineKind.Add:
                    var terminator = removedTerminators.Count > 0
                        ? removedTerminators.Dequeue()
                        : SelectAddedLineTerminator(
                            replacement,
                            source,
                            sourceIndex,
                            allSegments,
                            matchIndex,
                            fallbackLineEnding);
                    replacement.Add(new TextLineSegment(line.Text, terminator));
                    break;
                default:
                    throw new MutationValidationException("Invalid line-ending mutation state.");
            }
        }

        return replacement;
    }

    private static string SelectAddedLineTerminator(
        IReadOnlyList<TextLineSegment> replacement,
        IReadOnlyList<TextLineSegment> source,
        int sourceIndex,
        IReadOnlyList<TextLineSegment> allSegments,
        int matchIndex,
        string fallbackLineEnding)
    {
        var previous = replacement.LastOrDefault(static segment => segment.Terminator.Length > 0)?.Terminator;
        if (previous is not null)
        {
            return previous;
        }

        var following = source.Skip(sourceIndex)
            .FirstOrDefault(static segment => segment.Terminator.Length > 0)?.Terminator;
        if (following is not null)
        {
            return following;
        }

        if (matchIndex > 0 && allSegments[matchIndex - 1].Terminator.Length > 0)
        {
            return allSegments[matchIndex - 1].Terminator;
        }

        var afterIndex = matchIndex + source.Count;
        if (afterIndex < allSegments.Count && allSegments[afterIndex].Terminator.Length > 0)
        {
            return allSegments[afterIndex].Terminator;
        }

        return fallbackLineEnding;
    }

    private static void EnsureLineSeparationAtInsertionBoundary(
        List<TextLineSegment> segments,
        int insertionIndex,
        int insertedCount,
        string fallbackLineEnding)
    {
        if (insertedCount == 0)
        {
            return;
        }

        if (insertionIndex > 0 && segments[insertionIndex - 1].Terminator.Length == 0)
        {
            segments[insertionIndex - 1] = segments[insertionIndex - 1] with
            {
                Terminator = segments[insertionIndex].Terminator.Length > 0
                    ? segments[insertionIndex].Terminator
                    : fallbackLineEnding,
            };
        }

        var lastInserted = insertionIndex + insertedCount - 1;
        if (lastInserted + 1 < segments.Count && segments[lastInserted].Terminator.Length == 0)
        {
            segments[lastInserted] = segments[lastInserted] with
            {
                Terminator = segments[lastInserted + 1].Terminator.Length > 0
                    ? segments[lastInserted + 1].Terminator
                    : fallbackLineEnding,
            };
        }
    }

    private static void PreserveTrailingNewline(
        List<TextLineSegment> segments,
        bool hasTrailingNewline,
        string fallbackLineEnding)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var last = segments[^1];
        if (hasTrailingNewline && last.Terminator.Length == 0)
        {
            var nearby = segments.Take(segments.Count - 1)
                .LastOrDefault(static segment => segment.Terminator.Length > 0)?.Terminator;
            segments[^1] = last with { Terminator = nearby ?? fallbackLineEnding };
        }
        else if (!hasTrailingNewline && last.Terminator.Length > 0)
        {
            segments[^1] = last with { Terminator = string.Empty };
        }
    }

    private static IReadOnlyList<int> FindSequenceMatches(
        IReadOnlyList<string> content,
        IReadOnlyList<string> expected,
        int minimumIndex)
    {
        var matches = new List<int>();
        for (var index = Math.Max(0, minimumIndex);
             index <= content.Count - expected.Count;
             index++)
        {
            var matched = true;
            for (var offset = 0; offset < expected.Count; offset++)
            {
                if (!string.Equals(
                        content[index + offset],
                        expected[offset],
                        StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                matches.Add(index);
            }
        }

        return matches;
    }

    private sealed record TextMatch(int Start, int Length);

    private sealed record LogicalTextMap(string Text, IReadOnlyList<int> Boundaries);

    private static AgentToolResult ClassifyPermissionFailure(
        PermissionAuthorizationResult authorization,
        string toolName,
        string target)
    {
        var failure = authorization.Failure ?? AgentToolResult.Failure(
            $"Permission denied for mutation target '{target}'.");
        if (failure.FailureClassification != AgentToolFailureClassification.Unknown)
        {
            return failure;
        }

        var classification = authorization.Status switch
        {
            PermissionAuthorizationStatus.ExplicitlyDenied or
                PermissionAuthorizationStatus.ApprovalUnavailable or
                PermissionAuthorizationStatus.InvalidApproval =>
                AgentToolFailureClassification.Deterministic,
            _ => AgentToolFailureClassification.Unknown,
        };
        return AgentToolResult.Failure(
            failure.Error ?? $"Permission denied for mutation tool '{toolName}'.",
            failure.Output,
            failure.Metadata,
            classification);
    }

    private static FileMutationResult CreateResult(FileMutationPlan plan)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var created = plan.Operations
            .Where(static operation => operation.Kind == FileMutationKind.Add)
            .Select(static operation => operation.Source.RelativePath)
            .OrderBy(static path => path, comparer)
            .ToArray();
        var modified = plan.Operations
            .Where(static operation => operation.Kind == FileMutationKind.Update)
            .Select(static operation => operation.Source.RelativePath)
            .OrderBy(static path => path, comparer)
            .ToArray();
        var deleted = plan.Operations
            .Where(static operation => operation.Kind == FileMutationKind.Delete)
            .Select(static operation => operation.Source.RelativePath)
            .OrderBy(static path => path, comparer)
            .ToArray();
        var moved = plan.Operations
            .Where(static operation => operation.Kind == FileMutationKind.Move)
            .Select(static operation =>
                $"{operation.Source.RelativePath} -> {operation.Destination!.RelativePath}")
            .OrderBy(static path => path, comparer)
            .ToArray();
        var paths = plan.Operations
            .SelectMany(static operation => operation.Destination is null
                ? new[] { operation.Source.RelativePath }
                : new[] { operation.Source.RelativePath, operation.Destination.RelativePath })
            .Distinct(comparer)
            .OrderBy(static path => path, comparer)
            .ToArray();
        var before = plan.Operations
            .Where(static operation => operation.Before is not null)
            .OrderBy(static operation => operation.Source.RelativePath, comparer)
            .ToDictionary(
                static operation => operation.Source.RelativePath,
                static operation => operation.Before!.Sha256,
                comparer);
        var after = plan.Operations
            .Where(static operation => operation.Kind != FileMutationKind.Delete)
            .OrderBy(operation =>
                operation.Destination?.RelativePath ?? operation.Source.RelativePath,
                comparer)
            .ToDictionary(
                static operation =>
                    operation.Destination?.RelativePath ?? operation.Source.RelativePath,
                static operation => operation.AfterSha256,
                comparer);
        return new FileMutationResult(
            paths,
            created,
            modified,
            deleted,
            moved,
            plan.Diff.Additions,
            plan.Diff.Deletions,
            plan.Diff.Text,
            before,
            after);
    }

    private void ValidateFileSize(int byteCount, string relativePath)
    {
        if (byteCount > options.MaxFileBytes)
        {
            throw new MutationValidationException(
                $"The resulting file '{relativePath}' is {byteCount} bytes and exceeds the maximum mutation size of {options.MaxFileBytes} bytes.");
        }
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new MutationValidationException(
                "The expected SHA-256 value must contain exactly 64 hexadecimal characters.");
        }
    }

    private static string CreateArtifactPath(string directory, string extension)
    {
        string path;
        do
        {
            path = Path.Combine(directory, $".agentpulse-{Guid.NewGuid():N}{extension}");
        }
        while (File.Exists(path) || Directory.Exists(path));

        return path;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static bool IsInside(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        root = Path.GetFullPath(root);
        candidate = Path.GetFullPath(candidate);
        if (string.Equals(root, candidate, comparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    private static MutationValidationException StaleFile() =>
        new("The file changed after the mutation was planned. Read the latest contents and retry.");

    private static MutationValidationException WritePreconditionMismatch() =>
        new("The target file has changed or the provided expected SHA-256 value does not match.");

    private static string LowercaseFirst(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private sealed class StagedMutation(
        FileMutationOperation operation,
        string? temporaryPath,
        string? backupPath)
    {
        public FileMutationOperation Operation { get; } = operation;

        public string? TemporaryPath { get; } = temporaryPath;

        public string? BackupPath { get; } = backupPath;

        public bool CommitStarted { get; set; }

        public bool SourceBackedUp { get; set; }

        public bool TargetInstalled { get; set; }
    }
}
