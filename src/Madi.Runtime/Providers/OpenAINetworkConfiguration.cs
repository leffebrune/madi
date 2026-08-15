using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Madi.Networking;
using Madi.Runtime.Authentication;

namespace Madi.Runtime.Providers;

/// <summary>Codex endpoint and authentication settings for the general network client.</summary>
public static class OpenAINetworkConfiguration
{
    public static NetworkClientOptions Create(IAuthentication authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);

        return new NetworkClientOptions(
            new Uri("https://chatgpt.com/backend-api/codex/responses"))
        {
            UseBrotliCompression = true,
            DefaultHeaders = new Dictionary<string, string>
            {
                ["originator"] = "madi",
                ["User-Agent"] = CreateUserAgent(),
                ["OpenAI-Beta"] = "responses=experimental",
            },
            GetHeadersAsync = async cancellationToken =>
            {
                var accessToken = await authentication.GetAuthorizationAsync(cancellationToken);
                return new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {accessToken}",
                    ["chatgpt-account-id"] = GetAccountId(accessToken),
                };
            },
        };
    }

    private static string GetAccountId(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("The OAuth access token is not a valid JWT.");
        }

        var payload = parts[1]
            .Replace('-', '+')
            .Replace('_', '/')
            .PadRight((parts[1].Length + 3) / 4 * 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var document = JsonDocument.Parse(json);
        return document.RootElement
                   .GetProperty("https://api.openai.com/auth")
                   .GetProperty("chatgpt_account_id")
                   .GetString()
            ?? throw new InvalidOperationException("The OAuth token does not contain a ChatGPT account ID.");
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
