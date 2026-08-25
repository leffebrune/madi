using System.Runtime.CompilerServices;
using Madi.Core;
using Madi.Runtime.Agents;
using Madi.Runtime.Tools;

namespace Madi.Runtime.Tests;

public sealed class SubagentRunnerTests
{
    [Fact]
    public async Task RunAsyncSendsInstructionsComposedWithChildToolMetadata()
    {
        var provider = new RecordingProvider();
        var definition = new SubagentDefinition
        {
            Name = "reviewer",
            Description = "Reviews code changes.",
            Model = "openai/test:medium",
            Tools = ["read"],
            Instructions = "Review the requested code.",
        };
        var model = new AvailableModel
        {
            Name = "test",
            Provider = "openai",
            Api = "responses",
            MaxContextTokens = 1000,
        };
        var runner = new SubagentRunner(
            provider,
            [definition],
            [model],
            [new ReadTool()]);

        await runner.RunAsync("reviewer", "Review this change.");

        var request = Assert.Single(provider.Requests);
        Assert.Equal("read", Assert.Single(request.Tools).Name);
        Assert.StartsWith("Review the requested code.", request.Instructions);
        Assert.Contains("# Available tools", request.Instructions);
        Assert.Contains("## read", request.Instructions);
        Assert.Contains("\"path\"", request.Instructions);
    }

    private sealed class RecordingProvider : IModelProvider
    {
        public string Id => "test";
        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelResponseCompleted(
                new ModelResponse("response-1", "Reviewed.", []));
        }
    }
}
