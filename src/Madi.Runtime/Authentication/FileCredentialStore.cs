using System.Text.Json;

namespace Madi.Runtime.Authentication;

public sealed class FileCredentialStore : ICredentialStore
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private readonly string _path;

    public FileCredentialStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "madi",
            "auth.json"));
    }

    public async Task<OAuthCredential?> LoadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ValidateProviderId(providerId);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var credentials = await ReadAllAsync(cancellationToken);
            return credentials.GetValueOrDefault(providerId);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(
        string providerId,
        OAuthCredential credential,
        CancellationToken cancellationToken = default)
    {
        ValidateProviderId(providerId);
        ArgumentNullException.ThrowIfNull(credential);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var credentials = await ReadAllAsync(cancellationToken);
            credentials[providerId] = credential;
            await WriteAllAsync(credentials, cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ValidateProviderId(providerId);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var credentials = await ReadAllAsync(cancellationToken);
            if (credentials.Remove(providerId))
            {
                await WriteAllAsync(credentials, cancellationToken);
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<OAuthCredential?> UpdateAsync(
        string providerId,
        Func<OAuthCredential?, Task<OAuthCredential?>> update,
        CancellationToken cancellationToken = default)
    {
        ValidateProviderId(providerId);
        ArgumentNullException.ThrowIfNull(update);
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var credentials = await ReadAllAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var current = credentials.GetValueOrDefault(providerId);
            var replacement = await update(current);
            if (!ReferenceEquals(current, replacement))
            {
                if (replacement is null) credentials.Remove(providerId);
                else credentials[providerId] = replacement;
                // A refresh may have rotated the token; request cancellation must not discard it.
                await WriteAllAsync(credentials, CancellationToken.None);
            }
            return replacement;
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<Guid> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var devicePath = $"{_path}.device-id";
            if (File.Exists(devicePath))
            {
                return Guid.Parse(await File.ReadAllTextAsync(devicePath, cancellationToken));
            }
            var deviceId = Guid.NewGuid();
            var temporaryPath = $"{devicePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, deviceId.ToString(), CancellationToken.None);
                File.Move(temporaryPath, devicePath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return deviceId;
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream($"{_path}.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private async Task<Dictionary<string, OAuthCredential>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(_path, cancellationToken);
        return JsonSerializer.Deserialize(json, CredentialJsonContext.Default.DictionaryStringOAuthCredential) ?? [];
    }

    private async Task WriteAllAsync(
        Dictionary<string, OAuthCredential> credentials,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Credential path must include a directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(
                credentials,
                CredentialJsonContext.Default.DictionaryStringOAuthCredential);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("Provider ID is required.", nameof(providerId));
        }
    }
}
