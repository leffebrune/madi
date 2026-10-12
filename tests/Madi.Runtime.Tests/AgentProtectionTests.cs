using System.Runtime.CompilerServices;
using Madi.Core;

namespace Madi.Runtime.Tests;

public sealed class AgentProtectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoHundredRoundBoundaryPreservesConversation(bool finalAnswer)
    {
        var provider = new Provider(repeat: false, finalAnswer);
        var session = new Session { Id = "test", Model = new ModelOptions("test") };
        var events = await Run(new Agent(provider, "instructions"), session);
        Assert.Equal(200, provider.Requests.Count);
        Assert.Equal(finalAnswer ? 199 : 200, session.Conversation.OfType<ToolResult>().Count());
        Assert.Equal(session.Conversation.OfType<ModelToolCall>().Count(), session.Conversation.OfType<ToolResult>().Count());
        if (finalAnswer) Assert.Single(events.OfType<AgentResponseCompleted>());
        else Assert.Equal(AgentStopReason.RoundLimit, Assert.Single(events.OfType<AgentResponseStopped>()).Reason);
    }

    [Fact]
    public async Task RepetitionWarnsModelAndStopsWithCompleteResults()
    {
        var provider = new Provider(repeat: true, finalAnswer: false);
        var session = new Session { Id = "test", Model = new ModelOptions("test") };
        var events = await Run(new Agent(provider, "instructions"), session);
        Assert.Equal(5, provider.Requests.Count);
        Assert.Equal("instructions", provider.Requests[2].Instructions);
        Assert.Contains("Runtime warning", provider.Requests[3].Instructions);
        Assert.Equal(AgentStopReason.ToolLoop, Assert.Single(events.OfType<AgentResponseStopped>()).Reason);
        Assert.Equal(5, session.Conversation.OfType<ToolResult>().Count());
        Assert.Empty(events.OfType<AgentResponseCompleted>());
    }

    [Fact]
    public async Task ParallelCompletionOrderDoesNotAffectDetection()
    {
        var tool = new AlternatingParallelTool();
        var provider = new ParallelProvider();
        var session = new Session { Id = "parallel", Model = new ModelOptions("test") };
        var events = new List<AgentEvent>();
        await foreach (var item in new Agent(provider, "instructions", [tool]).RespondAsync(null, session, "run"))
        {
            events.Add(item);
            if (item is AgentToolExecutionCompleted) tool.ReleaseSecond();
        }
        Assert.Equal(AgentStopReason.ToolLoop, Assert.Single(events.OfType<AgentResponseStopped>()).Reason);
        Assert.Equal(10, session.Conversation.OfType<ToolResult>().Count());
        Assert.Equal(["0", "1", "1", "0", "0", "1", "1", "0", "0", "1"],
            events.OfType<AgentToolExecutionCompleted>().Select(e => e.Result));
        Assert.Equal(["0", "1", "0", "1", "0", "1", "0", "1", "0", "1"],
            session.Conversation.OfType<ToolResult>().Select(r => r.Output));
    }

    private sealed class ParallelProvider : IModelProvider
    {
        private int _round;
        public string Id => "test";
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            _round++;
            yield return new ModelResponseCompleted(new ModelResponse("r", "", [
                new ModelToolCall(Id, $"{_round}-0", "parallel", "{\"n\":0}", "{}"),
                new ModelToolCall(Id, $"{_round}-1", "parallel", "{\"n\":1}", "{}"),
            ]));
        }
    }

    private sealed class AlternatingParallelTool : ITool
    {
        private int _calls;
        private TaskCompletionSource _firstCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseSecond() => _firstCompleted.TrySetResult();
        public string Name => "parallel";
        public string Description => "test";
        public System.Text.Json.JsonElement Parameters => System.Text.Json.JsonSerializer.SerializeToElement(new { });
        public bool SupportsParallelExecution => true;
        public async Task<string> ExecuteAsync(System.Text.Json.JsonElement arguments, CancellationToken cancellationToken = default)
        {
            var invocation = _calls++;
            if (invocation % 2 == 0)
                _firstCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var signal = _firstCompleted;
            var n = arguments.GetProperty("n").GetInt32();
            var first = invocation / 2 % 2;
            if (n != first)
                await signal.Task.WaitAsync(cancellationToken);
            return n.ToString();
        }
    }

    private static async Task<List<AgentEvent>> Run(Agent agent, Session session)
    {
        var events = new List<AgentEvent>();
        await foreach (var item in agent.RespondAsync(null, session, "run")) events.Add(item);
        return events;
    }

    private sealed class Provider(bool repeat, bool finalAnswer) : IModelProvider
    {
        public string Id => "test";
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            var number = Requests.Count;
            // Unknown tool errors also participate in loop detection.
            ModelOutputItem[] output = finalAnswer && number == 200 ? [] :
                [new ModelToolCall(Id, number.ToString(), "missing", repeat ? "{}" : $"{{\"n\":{number}}}", "{}")];
            yield return new ModelResponseCompleted(new ModelResponse("r", "done", output));
        }
    }
}
