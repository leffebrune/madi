using System.Runtime.CompilerServices;
using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tests;

public sealed class AgentSteeringTests
{
    private static Session NewSession() => new() { Id = "steering", Model = new ModelOptions("test") };
    private static AgentInput Input(string text) => new(Guid.NewGuid(), text);

    [Fact]
    public async Task InputDuringFinalResponseContinuesInOrderAndCommitsOnce()
    {
        var queue = new AgentInputQueue();
        var first = Input("use C#"); var second = Input("also add tests");
        var provider = new Provider(round =>
        {
            if (round == 1) { queue.Enqueue(first); queue.Enqueue(second); }
            return [new ModelAssistantOutput("test", "answer " + round, "{}")];
        });
        var session = NewSession();
        var events = await Collect(new Agent(provider, "instructions"), session, queue);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Single(provider.Requests[0].Input);
        Assert.Equal(new[] { "initial", first.Text, second.Text },
            provider.Requests[1].Input.OfType<UserMessage>().Select(x => x.Text));
        Assert.Equal(new[] { first.Id, second.Id }, events.OfType<AgentInputIncluded>().Select(x => x.Input.Id));
        Assert.Equal(new[] { 2, 3 }, events.OfType<AgentInputIncluded>().Select(x => x.ConversationIndex));
        Assert.Single(events.OfType<AgentResponseCompleted>());
        Assert.Single(events.OfType<AgentAssistantCompleted>());
        Assert.Equal(5, session.Conversation.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeToolBatchFinishesBeforeInputIsInserted(bool parallel)
    {
        var queue = new AgentInputQueue();
        var tool = new Tool(parallel, () => queue.Enqueue(Input("steer")));
        var provider = new Provider(round => round == 1
            ? [new ModelToolCall("test", "one", "test", "{}", "{}"),
               new ModelToolCall("test", "two", "test", "{}", "{}")]
            : [new ModelAssistantOutput("test", "done", "{}")]);
        await Collect(new Agent(provider, "instructions", [tool]), NewSession(), queue);
        var conversation = provider.Requests[1].Input.ToArray();
        Assert.Equal(2, tool.Executions);
        Assert.IsType<ToolResult>(conversation[3]);
        Assert.IsType<ToolResult>(conversation[4]);
        Assert.All(conversation.Skip(5), item => Assert.IsType<UserMessage>(item));
    }

    [Fact]
    public void CloseAndLateArrivalHaveAnExplicitContinuationBoundary()
    {
        var queue = new AgentInputQueue();
        var first = Input("first"); var late = Input("late");
        Assert.True(queue.Enqueue(first)); Assert.False(queue.Enqueue(first));
        Assert.False(queue.TryClose());
        Assert.Equal(first, Assert.Single(queue.Drain()));
        Assert.True(queue.TryClose());
        queue.Enqueue(late);
        Assert.Empty(queue.Drain());
        Assert.Equal(late, queue.BeginNextTurn());
        Assert.Empty(queue.Drain());
    }

    [Fact]
    public async Task ProviderFailureDoesNotClaimDelivery()
    {
        var queue = new AgentInputQueue(); queue.Enqueue(Input("not delivered"));
        var events = new List<AgentEvent>(); var session = NewSession();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in new Agent(new FailingProvider(), "instructions")
                .RespondAsync(null, session, "initial", queue)) events.Add(item);
        });
        Assert.Empty(events.OfType<AgentInputIncluded>());
        Assert.Empty(session.Conversation);
    }

    [Fact]
    public async Task AdditionalInputCannotResetRoundLimit()
    {
        var queue = new AgentInputQueue();
        var provider = new Provider(round =>
        {
            queue.Enqueue(Input("continue " + round));
            return [new ModelAssistantOutput("test", "answer", "{}")];
        });
        var events = await Collect(new Agent(provider, "instructions"), NewSession(), queue);
        Assert.Equal(Agent.MaxToolRounds, provider.Requests.Count);
        Assert.Equal(AgentStopReason.RoundLimit, Assert.Single(events.OfType<AgentResponseStopped>()).Reason);
        Assert.Empty(events.OfType<AgentResponseCompleted>());
        Assert.Single(queue.Abandon());
    }

    private static async Task<List<AgentEvent>> Collect(Agent agent, Session session, AgentInputQueue queue)
    {
        var events = new List<AgentEvent>();
        await foreach (var item in agent.RespondAsync(null, session, "initial", queue)) events.Add(item);
        return events;
    }

    private sealed class Provider(Func<int, IReadOnlyList<ModelOutputItem>> respond) : IModelProvider
    {
        public string Id => "test";
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            yield return new ModelRequestStarted("request");
            var output = respond(Requests.Count);
            yield return new ModelTextDelta("answer");
            yield return new ModelResponseCompleted(new ModelResponse("response", "answer", output));
        }
    }

    private sealed class FailingProvider : IModelProvider
    {
        public string Id => "test";
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("cannot construct request");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class Tool(bool parallel, Action execute) : ITool
    {
        public int Executions { get; private set; }
        public string Name => "test";
        public string Description => "test";
        public bool SupportsParallelExecution => parallel;
        public JsonElement Parameters => JsonDocument.Parse("{}").RootElement.Clone();
        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Executions++; execute(); return Task.FromResult("done");
        }
    }
}
