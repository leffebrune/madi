using System.Runtime.InteropServices;
using Madi.Networking;
using Madi.Runtime.Authentication;

namespace Madi.Runtime.Providers;

/// <summary>Direct Responses API access using Sign in with ChatGPT for the Codex provider.</summary>
public static class OpenAINetworkConfiguration
{
    public static NetworkClientOptions Create(IAuthentication authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);

        return new NetworkClientOptions(
            new Uri("https://api.openai.com/v1/responses"))
        {
            DefaultHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = CreateUserAgent(),
            },
            GetHeadersAsync = async cancellationToken =>
            {
                var accessToken = await authentication.GetAuthorizationAsync(cancellationToken);
                return new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {accessToken}",
                };
            },
        };
    }

    private static string CreateUserAgent()
    {
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win32"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin" : "linux";
        var version = Environment.OSVersion.Version;
        var release = $"{version.Major}.{version.Minor}.{version.Build}";
        return $"madi ({platform} {release}; {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";
    }
}
