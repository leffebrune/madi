using System.Runtime.CompilerServices;
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

        var requestId = Guid.CreateVersion7().ToString();
        var headers = new Dictionary<string, string>
        {
            ["session-id"] = requestId,
            ["x-client-request-id"] = requestId,
        };

        await foreach (var item in OpenAIResponsesProtocol.StreamAsync(
                           _client,
                           ProviderId,
                           request,
                           headers,
                           includeCodexExtensions: true,
                           cancellationToken,
                           sendPromptCacheKey: _sendPromptCacheKey))
        {
            yield return item;
        }
    }
}
