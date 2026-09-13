using System.Runtime.CompilerServices;
using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tests;

public sealed class AgentLifecycleTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Fact]
    public async Task A05_CancellationWaitsForEveryActualParallelChildTask()
    {
        var tool = new WaitingTool(); var provider = new Provider(two: true);
        var agent = new Agent(provider, "instructions", [tool]);
        using var cancellation = new CancellationTokenSource();
        var session = new Session { Id = "A", Model = new ModelOptions("first") };
        var pump = Task.Run(async () => { await foreach (var _ in agent.RespondAsync(null, session, "run", cancellation.Token)) { } });
        await tool.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await tool.BothCleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pump.IsCompleted);
        tool.ReleaseCleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, tool.Cleaned); Assert.Empty(session.Conversation);
    }

    [Fact]
    public async Task A15_ModelAndWorkInstructionsStayFixedAcrossToolRounds()
    {
        var provider = new Provider(two: false);
        var work = new Work { Id = "W", Context = "before" };
        var session = new Session { Id = "A", WorkId = work.Id, Model = new ModelOptions("before") };
        var agent = new Agent(provider, "before", [new MutatingTool(() =>
        {
            work.Context = "after"; session.Model = new ModelOptions("after");
        })]);
        await foreach (var _ in agent.RespondAsync(work, session, "run")) { }
        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request => { Assert.Equal("before", request.Options.Model); Assert.Equal("before", request.WorkInstructions); });
    }

    private sealed class Provider(bool two) : IModelProvider
    {
        public string Id => "test";
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; Requests.Add(request);
            var calls = new List<ModelOutputItem>();
            if (Requests.Count == 1)
            {
                calls.Add(new ModelToolCall(Id, "one", "test", "{}", "{}"));
                if (two) calls.Add(new ModelToolCall(Id, "two", "test", "{}", "{}"));
            }
            yield return new ModelResponseCompleted(new ModelResponse("r", "done", calls));
        }
    }
    private sealed class WaitingTool : ITool
    {
        private int _started, _cleaning, _cleaned;
        public TaskCompletionSource BothStarted { get; } = Signal();
        public TaskCompletionSource BothCleaning { get; } = Signal();
        public TaskCompletionSource ReleaseCleanup { get; } = Signal();
        public int Cleaned => _cleaned;
        public string Name => "test"; public string Description => "test";
        public JsonElement Parameters => JsonDocument.Parse("{}").RootElement.Clone();
        public bool SupportsParallelExecution => true;
        public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _started) == 2) BothStarted.SetResult();
            try { await Signal().Task.WaitAsync(cancellationToken); return "unused"; }
            finally
            {
                if (Interlocked.Increment(ref _cleaning) == 2) BothCleaning.SetResult();
                await ReleaseCleanup.Task; Interlocked.Increment(ref _cleaned);
            }
        }
    }
    private sealed class MutatingTool(Action mutate) : ITool
    {
        public string Name => "test"; public string Description => "test";
        public JsonElement Parameters => JsonDocument.Parse("{}").RootElement.Clone();
        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) { mutate(); return Task.FromResult("done"); }
    }
}
