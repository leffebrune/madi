namespace Madi.Runtime.Authentication;

public sealed record OAuthCredential(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt);
