using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using AgentPulse.Application.Processes;

namespace AgentPulse.Infrastructure.Processes;

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(request.WorkingDirectory))
        {
            throw new ProcessStartException(
                request.Executable,
                new DirectoryNotFoundException(
                    $"The working directory '{request.WorkingDirectory}' does not exist."));
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request),
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                throw new ProcessStartException(
                    request.Executable,
                    new InvalidOperationException("The operating system did not start the process."));
            }
        }
        catch (Win32Exception exception) when (IsExecutableMissing(exception))
        {
            throw new ProcessExecutableNotFoundException(request.Executable, exception);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or
                UnauthorizedAccessException or SecurityException)
        {
            throw new ProcessStartException(request.Executable, exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = CreateTimeoutSource(request.Timeout);
        using var linkedSource = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;

            return new ProcessResult(process.ExitCode, standardOutput, standardError);
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            await StopProcessAsync(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Process execution was cancelled.",
                    innerException: null,
                    cancellationToken);
            }

            throw new ProcessTimeoutException(request.Executable, request.Timeout);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or Win32Exception)
        {
            await StopProcessAsync(process);
            throw new ProcessExecutionException(request.Executable, exception);
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static CancellationTokenSource? CreateTimeoutSource(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        return new CancellationTokenSource(timeout);
    }

    private static bool IsExecutableMissing(Win32Exception exception)
    {
        return exception.NativeErrorCode is 2 or 3;
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // The process may have exited between the state check and the kill request.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The process was never fully associated with an operating-system process.
        }
    }
}
