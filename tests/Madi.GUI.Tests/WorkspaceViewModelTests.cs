using Madi.App;
using Madi.GUI.Presentation;
using Madi.TestSupport;

namespace Madi.GUI.Tests;

public sealed class WorkspaceViewModelTests
{
    private sealed class Dispatcher : IUiDispatcher
    {
        private readonly object _gate = new();
        public Task InvokeAsync(Action action) { lock (_gate) action(); return Task.CompletedTask; }
    }
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task SessionSelectionSurvivesRefreshAndCancelClearsCheckboxes()
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync();
        await h.Workspace.CreateSessionAsync(tab.Id, null);
        using var view = new WorkspaceTabViewModel(h.Workspace, tab, new Dispatcher());
        await view.SynchronizeAsync();
        var current = view.Current!;
        var session = current.NavigationItems.First(x => x.SessionId is not null);
        current.SetSessionSelectionMode(true);
        current.SetSessionSelectedForDeletion(session.SessionId!, true);
        await current.RefreshNavigationAsync();
        Assert.True(current.CanDeleteSelectedSessions);
        Assert.Equal(1, current.SelectedSessionCount);
        Assert.True(current.NavigationItems.Single(x => x.SessionId == session.SessionId).IsSelectedForDeletion);
        Assert.All(current.NavigationItems.Where(x => x.IsHeader), x => Assert.False(x.ShowsDeleteCheckbox));
        Assert.All(current.NavigationItems.Where(x => x.SessionId is not null), x => Assert.True(x.ShowsDeleteCheckbox));
        current.SetSessionSelectionMode(false);
        Assert.False(current.CanDeleteSelectedSessions);
        Assert.Equal(0, current.SelectedSessionCount);
        Assert.All(current.NavigationItems, x =>
        {
            Assert.False(x.ShowsDeleteCheckbox);
            Assert.False(x.IsSelectedForDeletion);
        });
    }

    [Fact]
    public async Task BulkDeletionRemovesSelectedSessionsAndReplacesDeletedCurrentSessionOnce()
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync();
        var keep = tab.Selected!.SessionId;
        await h.Workspace.CreateSessionAsync(tab.Id, null);
        var first = tab.Selected!.SessionId;
        await h.Workspace.CreateSessionAsync(tab.Id, null);
        var second = tab.Selected!.SessionId;
        using var view = new WorkspaceTabViewModel(h.Workspace, tab, new Dispatcher());
        await view.SynchronizeAsync();
        var current = view.Current!;
        current.SetSessionSelectionMode(true);
        current.SetSessionSelectedForDeletion(first, true);
        current.SetSessionSelectedForDeletion(second, true);
        await current.DeleteSelectedSessionsAsync();
        await view.SynchronizeAsync();
        Assert.Equal(keep, Assert.Single(await h.Store.LoadSessionsAsync()).Id);
        Assert.Equal(keep, view.Current!.CurrentSessionId);
        Assert.False(current.IsSelectingSessions);
        Assert.Null(h.Workspace.FindRuntime(first));
        Assert.Null(h.Workspace.FindRuntime(second));
    }

    [Fact]
    public async Task BulkDeletionOfAllSessionsCreatesOneNewConversation()
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync();
        await h.Workspace.CreateSessionAsync(tab.Id, null);
        var ids = (await h.Store.LoadSessionsAsync()).Select(x => x.Id).ToArray();
        await h.Workspace.DeleteSessionsAsync(tab.Id, ids.Concat(ids));
        var remaining = Assert.Single(await h.Store.LoadSessionsAsync());
        Assert.DoesNotContain(remaining.Id, ids);
        Assert.Equal(remaining.Id, tab.Selected!.SessionId);
    }

    [Fact]
    public async Task BulkDeletionChecksOwnershipBeforeDeletingAnySessionAndKeepsSelectionOnFailure()
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync();
        var other = await h.Workspace.NewTabAsync(tab.Id);
        using var view = new WorkspaceTabViewModel(h.Workspace, tab, new Dispatcher());
        await view.SynchronizeAsync();
        var current = view.Current!;
        current.SetSessionSelectionMode(true);
        current.SetSessionSelectedForDeletion(tab.Selected!.SessionId, true);
        current.SetSessionSelectedForDeletion(other.Selected!.SessionId, true);
        await current.DeleteSelectedSessionsAsync();
        Assert.Equal(2, (await h.Store.LoadSessionsAsync()).Count);
        Assert.True(current.IsSelectingSessions);
        Assert.Equal(2, current.SelectedSessionCount);
        Assert.True(current.CanDeleteSelectedSessions);
        Assert.NotNull(current.ErrorText);
    }

    [Fact]
    public async Task BulkDeletionRejectsRunningSessionBeforeDeletingOtherTargets()
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync();
        var runningId = tab.Selected!.SessionId;
        var run = h.Workspace.ExecuteAsync(tab.Id, new SubmitPromptAction("bulk test"));
        await Wait(h.Agent.Run("bulk test").Started.Task);
        await h.Workspace.CreateSessionAsync(tab.Id, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Workspace.DeleteSessionsAsync(
            tab.Id, new[] { tab.Selected!.SessionId, runningId }));
        Assert.Equal(2, (await h.Store.LoadSessionsAsync()).Count);
        h.Agent.Run("bulk test").Release.SetResult();
        await Wait(run);
    }

    [Fact]
    public void A15_TypingDuringSaveAndLaterBroadcastArePreserved()
    {
        var editor = new WorkEditorState("base") { Draft = "submitted" };
        editor.Receive("submitted"); // Successful store broadcast.
        editor.Draft = "typed while awaiting UI dispatcher";
        editor.Receive("later remote save");
        editor.CompleteSave("submitted", "submitted");
        Assert.Equal("typed while awaiting UI dispatcher", editor.Draft);
        Assert.Equal("submitted", editor.Baseline);
        Assert.Equal("later remote save", editor.Latest);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task A03_A04_A14_BackgroundEventsAndViewStateStayWithTheirSession()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace; var tab = await w.InitializeAsync();
        using var view = new WorkspaceTabViewModel(w, tab, new Dispatcher()); await view.SynchronizeAsync();
        var a = view.Current!; a.Draft = "A";
        var send = a.SendOrCancelAsync(); await Wait(h.Agent.Run("A").Started.Task);
        a.Draft = "unsent A"; a.ScrollOffset = 123; a.FollowsLatest = false;
        var child = a.Timeline.Single(t => t.Kind == TimelineItemKind.Subagent); child.IsActivityExpanded = true;
        await w.CreateSessionAsync(tab.Id, null); await view.SynchronizeAsync(); var c = view.Current!;
        c.Draft = "unsent C";
        h.Agent.Run("A").Release.SetResult(); await Wait(send); await Wait(a.Runtime!.Completion);
        Assert.DoesNotContain(c.Timeline, t => t.Text.Contains("A")); Assert.Equal("unsent C", c.Draft);
        await w.SelectAsync(tab.Id, a.CurrentSessionId!); await view.SynchronizeAsync();
        Assert.Same(a, view.Current); Assert.Equal("unsent A", a.Draft);
        Assert.Equal(123, a.ScrollOffset); Assert.False(a.FollowsLatest); Assert.True(child.IsActivityExpanded);
        Assert.False(child.IsRunning); Assert.False(a.IsBusy);
    }

    [Fact]
    public async Task A07_RejectedSelectionKeepsDraftAndOffersOwnerNavigation()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); var two = await w.NewTabAsync(one.Id);
        using var view = new WorkspaceTabViewModel(w, two, new Dispatcher()); await view.SynchronizeAsync();
        var b = view.Current!; b.Draft = "keep me";
        await b.ActivateSessionAsync(one.Selected!.SessionId); await view.SynchronizeAsync();
        Assert.Same(b, view.Current); Assert.Equal("keep me", b.Draft); Assert.True(b.HasBlockedSession);
        Assert.Contains(one.Name, b.ErrorText);
        await b.GoToOwnerAsync(); Assert.Equal(one.Id, w.ActiveTab);
    }

    [Fact]
    public async Task A15_WorkEditorsShareWithinTabAndPreserveConflictingDraftsAcrossTabs()
    {
        await using var h = new WorkspaceHarness(); var w = h.Workspace;
        var one = await w.InitializeAsync(); await w.ExecuteAsync(one.Id, new CreateWorkAction("W"));
        var two = await w.NewTabAsync(one.Id);
        using var v1 = new WorkspaceTabViewModel(w, one, new Dispatcher()); await v1.SynchronizeAsync();
        using var v2 = new WorkspaceTabViewModel(w, two, new Dispatcher()); await v2.SynchronizeAsync();
        v1.Current!.WorkPromptDraft = "first"; v2.Current!.WorkPromptDraft = "second";
        await v1.Current.SaveWorkPromptAsync();
        Assert.Equal("second", v2.Current.WorkPromptDraft); Assert.True(v2.Current.HasWorkConflict);
        await v2.Current.SaveWorkPromptAsync();
        Assert.Equal("first", (await h.Store.LoadWorksAsync()).Single().Context);
        Assert.Equal("second", v2.Current.WorkPromptDraft);
        v2.Current.UseLatestWorkAsBaseline(); await v2.Current.SaveWorkPromptAsync();
        Assert.Equal("second", (await h.Store.LoadWorksAsync()).Single().Context);
        Assert.Equal("second", v1.Current.WorkPromptDraft);
        v1.Current.WorkPromptDraft = "local shared draft";
        await w.CreateSessionAsync(one.Id, one.Selected!.Controller.State.Work!.Id); await v1.SynchronizeAsync();
        Assert.Equal("local shared draft", v1.Current!.WorkPromptDraft); Assert.True(v1.HasUnsavedChanges);
    }
}
