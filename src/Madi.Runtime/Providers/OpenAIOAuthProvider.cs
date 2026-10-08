using System.Runtime.CompilerServices;
using System.Text;
using Madi.Core;
using Madi.Networking;

namespace Madi.Runtime.Providers;

public sealed class OpenAIOAuthProvider : IModelProvider
{
    public const string ProviderId = "openai-codex";

    private readonly INetworkClient _client;
    private readonly bool _sendPromptCacheKey;

    public OpenAIOAuthProvider(INetworkClient client, bool sendPromptCacheKey = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sendPromptCacheKey = sendPromptCacheKey;
    }

    public string Id => ProviderId;

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        request = request with
        {
            PromptCacheKey = string.Concat(request.PromptCacheKey.EnumerateRunes().Take(64).Select(rune => rune.ToString())),
        };

        var requestId = _sendPromptCacheKey && !string.IsNullOrWhiteSpace(request.PromptCacheKey)
            ? request.PromptCacheKey
            : Guid.CreateVersion7().ToString();
        var headers = new Dictionary<string, string>
        {
            ["session_id"] = requestId,
            ["x-client-request-id"] = requestId,
        };

        await using var events = OpenAIResponsesProtocol.StreamAsync(
                           _client,
                           ProviderId,
                           request,
                           headers,
                           includeCodexExtensions: true,
                           cancellationToken,
                           sendPromptCacheKey: _sendPromptCacheKey).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await events.MoveNextAsync();
            }
            catch (Exception error) when ((error is HttpRequestException or InvalidOperationException)
                && error.Message.Contains("subscription_sharing_usage_limit_exceeded", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{error.Message}\nCheck your ChatGPT usage: https://chatgpt.com/settings/usage", error);
            }
            if (!hasNext) break;
            yield return events.Current;
        }
    }
}
