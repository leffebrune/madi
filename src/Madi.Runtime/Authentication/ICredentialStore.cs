namespace Madi.Runtime.Authentication;

public interface ICredentialStore
{
    Task<OAuthCredential?> LoadAsync(string providerId, CancellationToken cancellationToken = default);

    Task SaveAsync(string providerId, OAuthCredential credential, CancellationToken cancellationToken = default);

    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}
