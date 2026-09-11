using System.Runtime.CompilerServices;
using System.Text.Json;
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
            [new ReadTool()],
            runtimeContextProvider: () => new AgentRuntimeContext(
                new DateOnly(2026, 8, 15), "/workspace", "Test OS", "/bin/bash"));

        await runner.RunAsync("reviewer", "Review this change.");

        var request = Assert.Single(provider.Requests);
        Assert.Equal("read", Assert.Single(request.Tools).Name);
        Assert.StartsWith("Review the requested code.", request.Instructions);
        Assert.Contains("# Available tools", request.Instructions);
        Assert.Contains("## read", request.Instructions);
        Assert.Contains("\"path\"", request.Instructions);
        Assert.Contains("Current date: 2026-08-15", request.Instructions);
        Assert.Contains("Working directory: \"/workspace\"", request.Instructions);
        Assert.Contains("OS: \"Test OS\"", request.Instructions);
        Assert.Contains("Shell: \"/bin/bash\"", request.Instructions);
    }

    [Fact]
    public async Task RunAsyncStreamsChildToolActivityBeforeReturningResponse()
    {
        var provider = new ToolCallingProvider();
        var definition = new SubagentDefinition
        {
            Name = "reviewer",
            Description = "Reviews code changes.",
            Model = "openai/test:medium",
            Tools = ["inspect"],
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
            [new InspectTool()]);
        var events = new List<SubagentRunEvent>();

        var response = await runner.RunAsync(
            "reviewer",
            "Review this change.",
            item =>
            {
                events.Add(item);
                return ValueTask.CompletedTask;
            });

        Assert.Equal("Reviewed.", response);
        var started = Assert.IsType<SubagentToolStarted>(events[0]);
        Assert.Equal("child-call", started.CallId);
        Assert.Equal("inspect", started.Name);
        Assert.Contains("target.cs", started.Arguments, StringComparison.Ordinal);
        var completed = Assert.IsType<SubagentToolCompleted>(events[1]);
        Assert.Equal("child-call", completed.CallId);
        Assert.False(completed.IsError);
    }

    private sealed class InspectTool : ITool
    {
        public string Name => "inspect";
        public string Description => "Inspects a target.";
        public JsonElement Parameters => JsonDocument.Parse("{}").RootElement.Clone();

        public Task<string> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(arguments.GetProperty("target").GetString() ?? string.Empty);
    }

    private sealed class ToolCallingProvider : IModelProvider
    {
        private int _requestCount;
        public string Id => "test";

        public async IAsyncEnumerable<ModelEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                yield return new ModelResponseCompleted(new ModelResponse(
                    "response-1",
                    string.Empty,
                    [new ModelToolCall(
                        "tool-1",
                        "child-call",
                        "inspect",
                        "{\"target\":\"target.cs\"}",
                        "{}") ]));
                yield break;
            }

            yield return new ModelResponseCompleted(
                new ModelResponse("response-2", "Reviewed.", []));
        }
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
