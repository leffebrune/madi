using System.Text.Json;

namespace Madi.Runtime.Authentication;

public sealed class FileCredentialStore : ICredentialStore
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private readonly string _path;

    public FileCredentialStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "madi",
            "auth.json");
    }

    public async Task<OAuthCredential?> LoadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ValidateProviderId(providerId);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
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
