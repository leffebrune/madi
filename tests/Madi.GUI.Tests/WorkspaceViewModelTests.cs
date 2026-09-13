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
        h.Agent.Run("A").Release.SetResult(); await Wait(send);
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
