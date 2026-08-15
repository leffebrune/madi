using Madi.Core;
using System.Runtime.CompilerServices;

namespace Madi.Runtime.Providers;

public sealed class OpenRouterProvider : IModelProvider
{
    public string Id => "openrouter";

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Placeholder adapter. It can be replaced without changing Agent or UI.
        await Task.Delay(1000, cancellationToken);
        var prompt = request.Input.OfType<UserMessage>().LastOrDefault()?.Text ?? string.Empty;
        var text = $"Response from OpenRouter for prompt: {prompt}";
        yield return new ModelTextDelta(text);
        var output = new ModelAssistantOutput(Id, text, "{}");
        yield return new ModelOutputItemCompleted(output);
        yield return new ModelResponseCompleted(
            new ModelResponse(string.Empty, text, [output]));
    }
}
