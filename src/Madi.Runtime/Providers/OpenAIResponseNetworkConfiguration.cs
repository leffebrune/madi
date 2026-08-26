using Madi.Networking;

namespace Madi.Runtime.Providers;

/// <summary>Network settings for an OpenAI-compatible Responses API endpoint.</summary>
public static class OpenAIResponseNetworkConfiguration
{
    public static NetworkClientOptions Create(string baseUrl, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException("The base URL must be an absolute URI.", nameof(baseUrl));
        }

        return Create(baseUri, apiKey);
    }

    public static NetworkClientOptions Create(Uri baseUrl, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ValidateApiKey(apiKey);

        var options = CreateOptions(baseUrl);
        return options with
        {
            DefaultHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {apiKey}",
            },
        };
    }

    private static NetworkClientOptions CreateOptions(Uri baseUrl)
    {
        ValidateBaseUrl(baseUrl);
        var endpoint = new Uri($"{baseUrl.AbsoluteUri.TrimEnd('/')}/responses");
        return new NetworkClientOptions(endpoint);
    }

    private static void ValidateBaseUrl(Uri baseUrl)
    {
        if (!baseUrl.IsAbsoluteUri
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(baseUrl.Host))
        {
            throw new ArgumentException(
                "The base URL must be an absolute HTTP or HTTPS URI with a host.",
                nameof(baseUrl));
        }

        if (baseUrl.Scheme == Uri.UriSchemeHttp && !baseUrl.IsLoopback)
        {
            throw new ArgumentException(
                "HTTP is allowed only for loopback endpoints; use HTTPS for remote hosts.",
                nameof(baseUrl));
        }

        if (!string.IsNullOrEmpty(baseUrl.UserInfo))
        {
            throw new ArgumentException(
                "The base URL must not contain user information.",
                nameof(baseUrl));
        }

        if (!string.IsNullOrEmpty(baseUrl.Query) || !string.IsNullOrEmpty(baseUrl.Fragment))
        {
            throw new ArgumentException(
                "The base URL must not contain a query string or fragment.",
                nameof(baseUrl));
        }
    }

    private static void ValidateApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (apiKey.Length <= 3
            || !apiKey.StartsWith("sk-", StringComparison.Ordinal)
            || apiKey.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "The API key must use the 'sk-' format.",
                nameof(apiKey));
        }
    }
}
