namespace Madi.Runtime.Authentication;

public interface ICredentialStore
{
    Task<OAuthCredential?> LoadAsync(string providerId, CancellationToken cancellationToken = default);

    Task SaveAsync(string providerId, OAuthCredential credential, CancellationToken cancellationToken = default);

    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Updates under the store lock. Once the update starts, its result must be persisted even if the caller cancels.</summary>
    Task<OAuthCredential?> UpdateAsync(
        string providerId,
        Func<OAuthCredential?, Task<OAuthCredential?>> update,
        CancellationToken cancellationToken = default);

    Task<Guid> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken = default);
}
