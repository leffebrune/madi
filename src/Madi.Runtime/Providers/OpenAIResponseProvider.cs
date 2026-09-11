using System.Runtime.CompilerServices;
using Madi.Core;
using Madi.Networking;

namespace Madi.Runtime.Providers;

/// <summary>Accesses an OpenAI-compatible Responses API with API key authentication.</summary>
public sealed class OpenAIResponseProvider : IModelProvider
{
    public const string ProviderId = "openai-responses";

    private readonly INetworkClient _client;
    private readonly bool _sendPromptCacheKey;

    public OpenAIResponseProvider(string baseUrl, string apiKey, bool sendPromptCacheKey = true)
        : this(new NetworkClient(OpenAIResponseNetworkConfiguration.Create(baseUrl, apiKey)), sendPromptCacheKey)
    {
    }

    public OpenAIResponseProvider(Uri baseUrl, string apiKey, bool sendPromptCacheKey = true)
        : this(new NetworkClient(OpenAIResponseNetworkConfiguration.Create(baseUrl, apiKey)), sendPromptCacheKey)
    {
    }

    public OpenAIResponseProvider(INetworkClient client, bool sendPromptCacheKey = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sendPromptCacheKey = sendPromptCacheKey;
    }

    public string Id => ProviderId;

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in OpenAIResponsesProtocol.StreamAsync(
                           _client,
                           ProviderId,
                           request,
                           cancellationToken: cancellationToken,
                           sendPromptCacheKey: _sendPromptCacheKey))
        {
            yield return item;
        }
    }
}
