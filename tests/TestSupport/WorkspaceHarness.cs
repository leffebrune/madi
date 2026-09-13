using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Madi.App;
using Madi.Core;
using Madi.Runtime.Authentication;
using Madi.Runtime.Persistence;

namespace Madi.TestSupport;

public sealed class WorkspaceHarness : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "madi-workspace-tests-" + Guid.NewGuid());
    public ControlledAgent Agent { get; } = new();
    public ControlledStore Store { get; }
    public WorkspaceCoordinator Workspace { get; }
    public WorkspaceHarness()
    {
        Directory.CreateDirectory(Root);
        Store = new ControlledStore(new FileWorkSessionStore(Root));
        Workspace = new WorkspaceCoordinator(new MadiController(Agent, Store,
            new ModelOptions("test", Provider: "test"),
            [new AvailableModel { Name = "test", Provider = "test", Api = "test", MaxContextTokens = 1000 }],
            new TestAuthentication(), Root));
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var run in Agent.Runs.Values) { run.Release.TrySetResult(); run.Cleanup.TrySetResult(); }
        Store.ReleaseCommit.TrySetResult();
        foreach (var load in Store.Loads.Values) load.TrySetResult();
        await Workspace.CloseAsync();
        Directory.Delete(Root, true);
    }
    private sealed class TestAuthentication : IOAuthAuthentication
    {
        private bool _loggedIn = true;
        public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default) =>
            _loggedIn ? Task.FromResult("test-only") : throw new InvalidOperationException("Login required");
        public Task LoginAsync(CancellationToken cancellationToken = default) { _loggedIn = true; return Task.CompletedTask; }
        public Task LogoutAsync(CancellationToken cancellationToken = default) { _loggedIn = false; return Task.CompletedTask; }
    }
}

public sealed class ControlledRun
{
    public TaskCompletionSource Started { get; } = NewSignal();
    public TaskCompletionSource Release { get; } = NewSignal();
    public TaskCompletionSource Cleanup { get; } = NewSignal();
    public TaskCompletionSource Cleaning { get; } = NewSignal();
    public bool DelayCleanup { get; set; }
    public bool EmitChild { get; set; } = true;
    public bool Cleaned { get; private set; }
    public string? WorkPrompt { get; set; }
    public static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task FinishAsync()
    {
        Cleaning.TrySetResult();
        if (DelayCleanup) await Cleanup.Task;
        Cleaned = true;
    }
}

public sealed class ControlledAgent : IAgent
{
    public ConcurrentDictionary<string, ControlledRun> Runs { get; } = new();
    public ControlledRun Run(string prompt) => Runs.GetOrAdd(prompt, _ => new ControlledRun());
    public string SystemInstructions { get; set; } = "deterministic test instructions";
    public async IAsyncEnumerable<AgentEvent> RespondAsync(Work? work, Session session, string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var run = Run(prompt); run.WorkPrompt = work?.Context;
        yield return new AgentTextDelta(prompt + " delta");
        if (run.EmitChild) yield return new AgentSubagentStarted(prompt + "-child", "test-child", prompt);
        run.Started.TrySetResult();
        try
        {
            await run.Release.Task.WaitAsync(cancellationToken);
            session.Conversation.AddRange([new UserMessage(prompt), new AssistantMessage(prompt + " complete")]);
            if (run.EmitChild) yield return new AgentSubagentCompleted(prompt + "-child", "test-child", "done", false);
            yield return new AgentResponseCompleted(prompt + " complete");
        }
        finally { await run.FinishAsync(); }
    }
}

public sealed class ControlledStore(IWorkSessionStore inner) : IWorkSessionStore
{
    public ConcurrentDictionary<string, TaskCompletionSource> Loads { get; } = new();
    public TaskCompletionSource LoadStarted { get; } = ControlledRun.NewSignal();
    public string? DelayCommitSession { get; set; }
    public TaskCompletionSource CommitStarted { get; } = ControlledRun.NewSignal();
    public TaskCompletionSource ReleaseCommit { get; } = ControlledRun.NewSignal();
    public async Task CommitTurnAsync(Session session, int startIndex, CancellationToken cancellationToken = default)
    {
        if (session.Id == DelayCommitSession)
        {
            CommitStarted.TrySetResult(); await ReleaseCommit.Task.WaitAsync(cancellationToken);
        }
        await inner.CommitTurnAsync(session, startIndex, cancellationToken);
    }
    public async Task<CurrentConversation?> LoadConversationAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (Loads.TryGetValue(sessionId, out var gate))
        {
            LoadStarted.TrySetResult(); await gate.Task.WaitAsync(cancellationToken);
        }
        return await inner.LoadConversationAsync(sessionId, cancellationToken);
    }
    public Task<IReadOnlyList<Work>> LoadWorksAsync(CancellationToken cancellationToken = default) => inner.LoadWorksAsync(cancellationToken);
    public Task<IReadOnlyList<Session>> LoadSessionsAsync(CancellationToken cancellationToken = default) => inner.LoadSessionsAsync(cancellationToken);
    public Task<CurrentConversation?> LoadCurrentAsync(CancellationToken cancellationToken = default) => inner.LoadCurrentAsync(cancellationToken);
    public Task SaveWorkAsync(Work work, CancellationToken cancellationToken = default) => inner.SaveWorkAsync(work, cancellationToken);
    public Task SaveSessionAsync(Session session, CancellationToken cancellationToken = default) => inner.SaveSessionAsync(session, cancellationToken);
    public Task AppendConversationAsync(Session session, int startIndex, CancellationToken cancellationToken = default) => inner.AppendConversationAsync(session, startIndex, cancellationToken);
    public Task RewriteConversationAsync(Session session, CancellationToken cancellationToken = default) => inner.RewriteConversationAsync(session, cancellationToken);
    public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => inner.DeleteSessionAsync(sessionId, cancellationToken);
    public Task<bool> DeleteWorkAsync(string workId, CancellationToken cancellationToken = default) => inner.DeleteWorkAsync(workId, cancellationToken);
    public Task SetCurrentAsync(Session session, CancellationToken cancellationToken = default) => inner.SetCurrentAsync(session, cancellationToken);
}
