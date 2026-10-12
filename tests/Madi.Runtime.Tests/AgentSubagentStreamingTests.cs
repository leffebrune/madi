using System.Runtime.CompilerServices;
using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tests;

public sealed class AgentSubagentStreamingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParallelSubagentsStreamChildToolsWithParentCorrelation(bool parallel)
    {
        var snapshot = new ProjectInstructions("/AGENTS.md", "project snapshot");
        var tool = new StreamingSubagentTool(parallel, snapshot);
        var agent = new Agent(
            new ParallelSubagentProvider(),
            "Helpful assistant.",
            [tool], projectInstructionLoader: new SnapshotLoader(snapshot));
        var session = new Session
        {
            Id = "session-1",
            Title = "Test",
            Model = new ModelOptions("test"),
        };
        var events = new List<AgentEvent>();

        await foreach (var item in agent.RespondAsync(null, session, "delegate"))
        {
            events.Add(item);
        }

        var starts = events.OfType<AgentSubagentToolStarted>().ToArray();
        Assert.Equal(2, starts.Length);
        Assert.Contains(starts, item => item.SubagentCallId == "parent-1"
            && item.ToolCallId == "child-shared");
        Assert.Contains(starts, item => item.SubagentCallId == "parent-2"
            && item.ToolCallId == "child-shared");

        var childCompletions = events.OfType<AgentSubagentToolCompleted>().ToArray();
        Assert.Equal(2, childCompletions.Length);
        Assert.All(childCompletions, item => Assert.False(item.IsError));
        Assert.Equal(2, events.OfType<AgentSubagentCompleted>().Count());
        Assert.IsType<AgentResponseCompleted>(events[^1]);
    }

    private sealed class SnapshotLoader(ProjectInstructions snapshot) : IProjectInstructionLoader
    {
        public Task<ProjectInstructions?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectInstructions?>(snapshot);
    }

    private sealed class StreamingSubagentTool(bool parallel, ProjectInstructions expected) : ISubagentTool
    {
        public string Name => "subagent";
        public string Description => "Delegates work.";
        public JsonElement Parameters => JsonDocument.Parse("{}").RootElement.Clone();
        public bool SupportsParallelExecution => parallel;

        public string GetSubagentName(JsonElement arguments) =>
            arguments.GetProperty("name").GetString()!;

        public string GetSubagentPrompt(JsonElement arguments) =>
            arguments.GetProperty("task").GetString()!;

        public Task<string> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("done");

        public Task<string> ExecuteAsync(
            JsonElement arguments,
            ProjectInstructions? projectInstructions,
            Func<SubagentRunEvent, ValueTask> onEvent,
            CancellationToken cancellationToken = default)
        {
            Assert.Same(expected, projectInstructions);
            return ExecuteAsync(arguments, onEvent, cancellationToken);
        }

        public async Task<string> ExecuteAsync(
            JsonElement arguments,
            Func<SubagentRunEvent, ValueTask> onEvent,
            CancellationToken cancellationToken = default)
        {
            await onEvent(new SubagentToolStarted(
                "child-shared",
                "read",
                "{\"path\":\"file.cs\"}"));
            await Task.Yield();
            await onEvent(new SubagentToolCompleted(
                "child-shared",
                "read",
                IsError: false,
                TimeSpan.FromMilliseconds(5)));
            return "done";
        }
    }

    private sealed class ParallelSubagentProvider : IModelProvider
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
                    [
                        new ModelToolCall(
                            "tool-1",
                            "parent-1",
                            "subagent",
                            "{\"name\":\"reviewer\",\"task\":\"first\"}",
                            "{}"),
                        new ModelToolCall(
                            "tool-2",
                            "parent-2",
                            "subagent",
                            "{\"name\":\"scanner\",\"task\":\"second\"}",
                            "{}"),
                    ]));
                yield break;
            }

            yield return new ModelResponseCompleted(
                new ModelResponse("response-2", "complete", []));
        }
    }
}
