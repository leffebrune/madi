using Madi.App;
using Madi.Core;
using Madi.TestSupport;

namespace Madi.App.Tests;

public sealed class WorkspaceCoordinatorTests
{
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task A02_OneTabCanRunTwoSessionsAndBothPersistExactlyOnce()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace; var tab = await w.InitializeAsync();
        var a = tab.Selected!;
        var first = w.ExecuteAsync(tab.Id, new SubmitPromptAction("first")); await Wait(h.Agent.Run("first").Started.Task);
        await w.CreateSessionAsync(tab.Id, null); var b = tab.Selected!;
        var second = w.ExecuteAsync(tab.Id, new SubmitPromptAction("second")); await Wait(h.Agent.Run("second").Started.Task);
        Assert.Equal(2, w.RunningCount(tab.Id)); Assert.False(first.IsCompleted);
        h.Agent.Run("second").Release.SetResult(); await Wait(second);
        Assert.True(a.IsRunning); Assert.False(b.IsRunning);
        h.Agent.Run("first").Release.SetResult(); await Wait(first);
        foreach (var runtime in new[] { a, b })
        {
            var stored = (await h.Store.LoadConversationAsync(runtime.SessionId))!.Session;
            Assert.Equal(2, stored.Conversation.Count);
            Assert.Single(runtime.EventsAfter(0).Select(e => e.RunId).Distinct());
        }
        Assert.Null(w.Ownership.Owner(a.SessionId));
    }

    [Fact]
    public async Task A12_A18_CanceledLoadAndShutdownLeaveNoReservationAndPreserveActivePointer()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); var a = one.Selected!;
        var two = await w.NewTabAsync(one.Id); var b = two.Selected!;
        await w.CreateSessionAsync(two.Id, null); var c = two.Selected!;
        h.Store.Loads[b.SessionId] = ControlledRun.NewSignal();
        using var cancellation = new CancellationTokenSource();
        var loading = w.SelectAsync(one.Id, b.SessionId, cancellation.Token); await Wait(h.Store.LoadStarted.Task);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Same(a, one.Selected); Assert.Null(w.Ownership.Owner(b.SessionId));
        await w.CloseAsync();
        Assert.Empty(w.Tabs); Assert.Equal(0, w.Ownership.ClaimCount);
        Assert.Equal(c.SessionId, (await h.Store.LoadCurrentAsync())!.Session.Id);
        await w.CloseAsync(); // Idempotent global teardown.
        Assert.Equal(c.SessionId, (await h.Store.LoadCurrentAsync())!.Session.Id);
    }

    [Fact]
    public async Task A23_OAuthLogoutIsSharedAndCannotInvalidateAnActiveRun()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); var two = await w.NewTabAsync(one.Id);
        var active = w.ExecuteAsync(one.Id, new SubmitPromptAction("oauth")); await Wait(h.Agent.Run("oauth").Started.Task);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.ExecuteAsync(two.Id, new LogoutAction()));
        await w.CreateSessionAsync(two.Id, null); // Authentication guard does not block navigation.
        h.Agent.Run("oauth").Release.SetResult(); await Wait(active);
        await w.ExecuteAsync(two.Id, new LogoutAction());
        Assert.Equal(AuthenticationState.ActionRequired, one.Selected!.Controller.State.Authentication);
        await w.ExecuteAsync(two.Id, new LoginAction());
        Assert.Equal(AuthenticationState.Ready, one.Selected.Controller.State.Authentication);
    }

    [Fact]
    public async Task A04_LoneDeltaIsFlushedBeforeTheNextProviderEvent()
    {
        await using var h = new WorkspaceHarness(); var tab = await h.Workspace.InitializeAsync();
        var runtime = tab.Selected!; h.Agent.Run("sparse").EmitChild = false;
        var visible = ControlledRun.NewSignal();
        runtime.Changed += (_, _) =>
        {
            if (runtime.EventsAfter(0).Any(e => e.Event is AppAssistantDelta)) visible.TrySetResult();
        };
        var run = h.Workspace.ExecuteAsync(tab.Id, new SubmitPromptAction("sparse"));
        await Wait(visible.Task); Assert.False(run.IsCompleted);
        h.Agent.Run("sparse").Release.SetResult(); await Wait(run);
    }

    [Fact]
    public async Task A02_A03_A05_A06_A08_ParallelRunsAndCancelAreSessionScoped()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); var a = one.Selected!;
        var runA = w.ExecuteAsync(one.Id, new SubmitPromptAction("A")); await Wait(h.Agent.Run("A").Started.Task);
        var two = await w.NewTabAsync(one.Id); var b = two.Selected!;
        h.Agent.Run("B").DelayCleanup = true;
        var runB = w.ExecuteAsync(two.Id, new SubmitPromptAction("B")); await Wait(h.Agent.Run("B").Started.Task);
        Assert.False(runA.IsCompleted); Assert.False(runB.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.ExecuteAsync(one.Id, new SubmitPromptAction("duplicate")));
        await w.CreateSessionAsync(one.Id, null); var c = one.Selected!;
        Assert.Empty(c.EventsAfter(0));
        await Assert.ThrowsAsync<SessionInUseException>(() => w.SelectAsync(two.Id, a.SessionId));
        Assert.Same(b, two.Selected);
        await w.SelectAsync(one.Id, a.SessionId); Assert.Same(a, one.Selected);
        b.Cancel(); await Wait(h.Agent.Run("B").Cleaning.Task);
        Assert.False(runB.IsCompleted); Assert.True(a.IsRunning);
        h.Agent.Run("B").Cleanup.SetResult(); await Wait(runB);
        Assert.True(h.Agent.Run("B").Cleaned); Assert.False(runA.IsCompleted);
        h.Agent.Run("A").Release.SetResult(); await Wait(runA);
        Assert.Equal(2, (await h.Store.LoadConversationAsync(a.SessionId))!.Session.Conversation.Count);
        Assert.Empty((await h.Store.LoadConversationAsync(b.SessionId))!.Session.Conversation);
        Assert.All(a.EventsAfter(0), e => Assert.Equal(a.SessionId, e.SessionId));
        Assert.DoesNotContain(a.EventsAfter(0), e => e.Event is AppAssistantDelta d && d.Text.Contains("B"));
    }

    [Fact]
    public async Task A09_A10_A20_SaveRetainsClaimAndBackgroundCompletionDoesNotChangePointer()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); var a = one.Selected!;
        h.Store.DelayCommitSession = a.SessionId;
        var run = w.ExecuteAsync(one.Id, new SubmitPromptAction("A")); await Wait(h.Agent.Run("A").Started.Task);
        var two = await w.NewTabAsync(one.Id); var b = two.Selected!;
        await w.CreateSessionAsync(one.Id, null);
        h.Agent.Run("A").Release.SetResult(); await Wait(h.Store.CommitStarted.Task);
        Assert.Equal(one.Id, w.Ownership.Owner(a.SessionId));
        await Assert.ThrowsAsync<SessionInUseException>(() => w.SelectAsync(two.Id, a.SessionId));
        h.Store.ReleaseCommit.SetResult(); await Wait(run);
        Assert.Null(w.Ownership.Owner(a.SessionId));
        Assert.Equal(b.SessionId, (await h.Store.LoadCurrentAsync())!.Session.Id);
        await w.SelectAsync(two.Id, a.SessionId);
        Assert.Equal(2, two.Selected!.InitialConversation.Entries.Count);
    }

    [Fact]
    public async Task A07_A12_NewTabRejectionAndSlowSelectionLeaveNoTabOrReservation()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); var a = one.Selected!;
        await Assert.ThrowsAsync<SessionInUseException>(() => w.NewTabAsync(one.Id, a.SessionId));
        Assert.Single(w.Tabs); Assert.Same(a, one.Selected);
        var two = await w.NewTabAsync(one.Id); var b = two.Selected!;
        await w.CreateSessionAsync(two.Id, null); var c = two.Selected!;
        await w.CloseTabAsync(two.Id);
        h.Store.Loads[b.SessionId] = ControlledRun.NewSignal();
        var slow = w.SelectAsync(one.Id, b.SessionId); await Wait(h.Store.LoadStarted.Task);
        Assert.Same(a, one.Selected);
        await w.SelectAsync(one.Id, c.SessionId);
        h.Store.Loads[b.SessionId].SetResult(); Assert.False(await slow);
        Assert.Equal(c.SessionId, one.Selected!.SessionId); Assert.Null(w.Ownership.Owner(b.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.SelectAsync(one.Id, "missing"));
        Assert.Equal(c.SessionId, one.Selected.SessionId); Assert.Null(w.Ownership.Owner("missing"));
    }

    [Fact]
    public async Task A15_A16_WorkConflictAndDeletionAreAtomicAcrossChildren()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); await w.ExecuteAsync(one.Id, new CreateWorkAction("shared"));
        var workId = one.Selected!.Controller.State.Work!.Id;
        var two = await w.NewTabAsync(one.Id);
        Assert.Equal(workId, two.Selected!.Controller.State.Work!.Id);
        await w.SaveWorkAsync(one.Id, workId, "", "first");
        var run = w.ExecuteAsync(one.Id, new SubmitPromptAction("A")); await Wait(h.Agent.Run("A").Started.Task);
        Assert.Equal("first", h.Agent.Run("A").WorkPrompt);
        await Assert.ThrowsAsync<WorkConflictException>(() => w.SaveWorkAsync(two.Id, workId, "", "stale"));
        await w.SaveWorkAsync(two.Id, workId, "first", "second");
        Assert.Equal("first", h.Agent.Run("A").WorkPrompt);
        var ids = (await h.Store.LoadSessionsAsync()).Select(s => s.Id).ToArray();
        await Assert.ThrowsAsync<SessionInUseException>(() => w.ExecuteAsync(two.Id, new DeleteWorkAction(workId)));
        Assert.Equal(ids, (await h.Store.LoadSessionsAsync()).Select(s => s.Id));
        h.Agent.Run("A").Release.SetResult(); await Wait(run);
        var next = w.ExecuteAsync(one.Id, new SubmitPromptAction("next")); await Wait(h.Agent.Run("next").Started.Task);
        Assert.Equal("second", h.Agent.Run("next").WorkPrompt); h.Agent.Run("next").Release.SetResult(); await Wait(next);
    }

    [Fact]
    public async Task A17_A18_A22_CloseWaitsForChildrenAndOnlyCancelsItsOwnRuns()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); var two = await w.NewTabAsync(one.Id);
        h.Agent.Run("A").DelayCleanup = true;
        var a = w.ExecuteAsync(one.Id, new SubmitPromptAction("A")); var b = w.ExecuteAsync(two.Id, new SubmitPromptAction("B"));
        await Wait(Task.WhenAll(h.Agent.Run("A").Started.Task, h.Agent.Run("B").Started.Task));
        var closing = w.CloseTabAsync(one.Id); await Wait(h.Agent.Run("A").Cleaning.Task);
        Assert.False(closing.IsCompleted); Assert.NotNull(w.Ownership.Owner(one.Selected!.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.ExecuteAsync(one.Id, new SubmitPromptAction("late")));
        Assert.False(b.IsCompleted);
        h.Agent.Run("A").Cleanup.SetResult(); await Wait(closing); await Wait(a);
        Assert.Null(w.Ownership.Owner(one.Selected.SessionId));
        h.Agent.Run("B").Release.SetResult(); await Wait(b);
        for (var i = 0; i < 8; i++) { var tab = await w.NewTabAsync(two.Id); await w.CloseTabAsync(tab.Id); }
        await w.CloseAsync(); Assert.Empty(w.Tabs); Assert.Equal(0, w.Ownership.ClaimCount);
    }

    [Fact]
    public async Task A21_ReleasedSessionReloadsAfterAnotherTabChangesIt()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); var a = one.Selected!;
        await w.CreateSessionAsync(one.Id, null);
        var two = await w.NewTabAsync(one.Id, a.SessionId);
        var run = w.ExecuteAsync(two.Id, new SubmitPromptAction("new data")); await Wait(h.Agent.Run("new data").Started.Task);
        h.Agent.Run("new data").Release.SetResult(); await Wait(run);
        await w.CreateSessionAsync(two.Id, null);
        await w.SelectAsync(one.Id, a.SessionId);
        Assert.NotSame(a, one.Selected); Assert.Equal(2, one.Selected!.InitialConversation.Entries.Count);
    }
}
