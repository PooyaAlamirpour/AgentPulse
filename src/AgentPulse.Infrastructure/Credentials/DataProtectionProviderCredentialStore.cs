using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace AgentPulse.Infrastructure.Credentials;

public sealed class DataProtectionProviderCredentialStore : IProviderCredentialStore
{
    private const string Purpose = "AgentPulse.ProviderCredential.v1";
    private const string CredentialFileName = "xiaomi-mimo.credential";
    private const string KeyRingDirectoryName = "keyring";

    private static readonly UnixFileMode DirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;

    private static readonly UnixFileMode CredentialFileMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite;

    private readonly string _rootPath;
    private readonly string _credentialPath;
    private readonly string _keyRingPath;
    private readonly IDataProtector _protector;

    public DataProtectionProviderCredentialStore(ProviderCredentialStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _rootPath = options.RootPath;
        _credentialPath = Path.Combine(_rootPath, CredentialFileName);
        _keyRingPath = Path.Combine(_rootPath, KeyRingDirectoryName);

        EnsureSecureDirectory(_rootPath);
        EnsureSecureDirectory(_keyRingPath);

        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(_keyRingPath),
            builder =>
            {
                builder.SetApplicationName("AgentPulse");

                if (OperatingSystem.IsWindows())
                {
                    builder.ProtectKeysWithDpapi();
                }
            });
        _protector = provider.CreateProtector(Purpose);
    }

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_credentialPath))
        {
            return null;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(
                _credentialPath,
                cancellationToken);
            var plaintextBytes = _protector.Unprotect(protectedBytes);
            var credential = Encoding.UTF8.GetString(plaintextBytes).Trim();

            if (string.IsNullOrWhiteSpace(credential))
            {
                throw new ProviderCredentialStoreException(
                    "The stored Xiaomi MiMo credential is empty or corrupt.");
            }

            return credential;
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
                "The stored Xiaomi MiMo credential could not be read or decrypted.",
                exception);
        }
    }

    public async Task SaveAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        var normalizedCredential = credential.Trim();

        EnsureSecureDirectory(_rootPath);
        EnsureSecureDirectory(_keyRingPath);

        var temporaryPath = Path.Combine(
            _rootPath,
            $".{CredentialFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(normalizedCredential);
            var protectedBytes = _protector.Protect(plaintextBytes);

            var streamOptions = new FileStreamOptions
            {
                Mode = System.IO.FileMode.CreateNew,
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

            File.Move(temporaryPath, _credentialPath, overwrite: true);
            ApplySecureFileMode(_credentialPath);
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
                "The Xiaomi MiMo credential could not be stored securely.",
                exception);
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (File.Exists(_credentialPath))
            {
                File.Delete(_credentialPath);
            }

            return Task.CompletedTask;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ProviderCredentialStoreException(
                "The stored Xiaomi MiMo credential could not be removed.",
                exception);
        }
    }

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(_credentialPath));
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
