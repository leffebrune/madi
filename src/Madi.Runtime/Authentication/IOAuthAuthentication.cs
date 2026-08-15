namespace Madi.Runtime.Authentication;

public interface IOAuthAuthentication : IAuthentication
{
    Task LoginAsync(CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);
}
