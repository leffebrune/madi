namespace Madi.Runtime.Authentication;

/// <summary>
/// Supplies authorization material without exposing a concrete login mechanism.
/// </summary>
public interface IAuthentication
{
    Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default);
}
