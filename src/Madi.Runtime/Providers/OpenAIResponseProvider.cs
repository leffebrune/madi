using System.Runtime.CompilerServices;
using Madi.Core;
using Madi.Networking;

namespace Madi.Runtime.Providers;

/// <summary>Accesses an OpenAI-compatible Responses API with API key authentication.</summary>
public sealed class OpenAIResponseProvider : IModelProvider
{
    public const string ProviderId = "openai-responses";

    private readonly INetworkClient _client;

    public OpenAIResponseProvider(string baseUrl, string apiKey)
        : this(new NetworkClient(OpenAIResponseNetworkConfiguration.Create(baseUrl, apiKey)))
    {
    }

    public OpenAIResponseProvider(Uri baseUrl, string apiKey)
        : this(new NetworkClient(OpenAIResponseNetworkConfiguration.Create(baseUrl, apiKey)))
    {
    }

    public OpenAIResponseProvider(INetworkClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
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
                           cancellationToken: cancellationToken))
        {
            yield return item;
        }
    }
}
