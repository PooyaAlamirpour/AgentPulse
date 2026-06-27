using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace AgentPulse.Infrastructure.Credentials;

public sealed class DataProtectionProviderCredentialStore :
    IProviderCredentialStore,
    ILegacyProviderCredentialStore
{
    private const string LegacyPurpose = "AgentPulse.ProviderCredential.v1";
    private const string ScopedPurpose = "AgentPulse.ProviderCredential.v2";
    private const string LegacyCredentialFileName = "xiaomi-mimo.credential";
    private const string KeyRingDirectoryName = "keyring";

    private static readonly UnixFileMode DirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;

    private static readonly UnixFileMode CredentialFileMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite;

    private readonly string _rootPath;
    private readonly string _legacyCredentialPath;
    private readonly string _keyRingPath;
    private readonly IDataProtectionProvider _provider;
    private readonly IDataProtector _legacyProtector;

    public DataProtectionProviderCredentialStore(ProviderCredentialStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _rootPath = options.RootPath;
        _legacyCredentialPath = Path.Combine(_rootPath, LegacyCredentialFileName);
        _keyRingPath = Path.Combine(_rootPath, KeyRingDirectoryName);

        EnsureSecureDirectory(_rootPath);
        EnsureSecureDirectory(_keyRingPath);

        _provider = DataProtectionProvider.Create(
            new DirectoryInfo(_keyRingPath),
            builder =>
            {
                builder.SetApplicationName("AgentPulse");

                if (OperatingSystem.IsWindows())
                {
                    builder.ProtectKeysWithDpapi();
                }
            });
        _legacyProtector = _provider.CreateProtector(LegacyPurpose);
    }

    public Task<string?> GetAsync(
        ProviderCredentialScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return ReadCredentialAsync(
            GetScopedCredentialPath(scope),
            GetScopedProtector(scope),
            "The stored model endpoint credential",
            cancellationToken);
    }

    public Task SaveAsync(
        ProviderCredentialScope scope,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return WriteCredentialAsync(
            GetScopedCredentialPath(scope),
            GetScopedProtector(scope),
            credential,
            "The model endpoint credential",
            cancellationToken);
    }

    public Task DeleteAsync(
        ProviderCredentialScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return DeleteFileAsync(
            GetScopedCredentialPath(scope),
            "The stored model endpoint credential",
            cancellationToken);
    }

    public Task<bool> ExistsAsync(
        ProviderCredentialScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetScopedCredentialPath(scope)));
    }

    public Task<string?> GetLegacyAsync(CancellationToken cancellationToken = default)
    {
        return ReadCredentialAsync(
            _legacyCredentialPath,
            _legacyProtector,
            "The legacy Xiaomi MiMo credential",
            cancellationToken);
    }

    public Task DeleteLegacyAsync(CancellationToken cancellationToken = default)
    {
        return DeleteFileAsync(
            _legacyCredentialPath,
            "The legacy Xiaomi MiMo credential",
            cancellationToken);
    }

    public Task<bool> LegacyExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(_legacyCredentialPath));
    }

    public Task SaveLegacyAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        return WriteCredentialAsync(
            _legacyCredentialPath,
            _legacyProtector,
            credential,
            "The legacy Xiaomi MiMo credential",
            cancellationToken);
    }

    private async Task<string?> ReadCredentialAsync(
        string path,
        IDataProtector protector,
        string safeDescription,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var plaintextBytes = protector.Unprotect(protectedBytes);
            var credential = Encoding.UTF8.GetString(plaintextBytes);

            try
            {
                return ProviderCredentialValidator.ValidateAndNormalize(credential);
            }
            catch (ProviderCredentialValidationException exception)
            {
                throw new ProviderCredentialStoreException(
                    $"{safeDescription} is empty or corrupt.",
                    exception);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderCredentialStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            CryptographicException or
            DecoderFallbackException)
        {
            throw new ProviderCredentialStoreException(
                $"{safeDescription} could not be read or decrypted.",
                exception);
        }
    }

    private async Task WriteCredentialAsync(
        string path,
        IDataProtector protector,
        string credential,
        string safeDescription,
        CancellationToken cancellationToken)
    {
        var normalizedCredential = ProviderCredentialValidator.ValidateAndNormalize(credential);

        EnsureSecureDirectory(_rootPath);
        EnsureSecureDirectory(_keyRingPath);

        var temporaryPath = Path.Combine(
            _rootPath,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(normalizedCredential);
            var protectedBytes = protector.Protect(plaintextBytes);

            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };

            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = CredentialFileMode;
            }

            await using (var stream = new FileStream(temporaryPath, streamOptions))
            {
                ApplySecureFileMode(temporaryPath);
                await stream.WriteAsync(protectedBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
            ApplySecureFileMode(path);
            HardenKeyRingFiles();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            CryptographicException)
        {
            TryDelete(temporaryPath);
            throw new ProviderCredentialStoreException(
                $"{safeDescription} could not be stored securely.",
                exception);
        }
    }

    private static Task DeleteFileAsync(
        string path,
        string safeDescription,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.CompletedTask;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ProviderCredentialStoreException(
                $"{safeDescription} could not be removed.",
                exception);
        }
    }

    private string GetScopedCredentialPath(ProviderCredentialScope scope)
    {
        return Path.Combine(_rootPath, $"provider-{scope.FileId}.credential");
    }

    private IDataProtector GetScopedProtector(ProviderCredentialScope scope)
    {
        return _provider.CreateProtector(ScopedPurpose, scope.CanonicalValue);
    }

    private static void EnsureSecureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, DirectoryMode);
        File.SetUnixFileMode(path, DirectoryMode);
    }

    private static void ApplySecureFileMode(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, CredentialFileMode);
        }
    }

    private void HardenKeyRingFiles()
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(_keyRingPath))
        {
            return;
        }

        File.SetUnixFileMode(_keyRingPath, DirectoryMode);

        foreach (var file in Directory.EnumerateFiles(_keyRingPath))
        {
            File.SetUnixFileMode(file, CredentialFileMode);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
