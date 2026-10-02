using Madi.App;
using Madi.Core;
using Madi.TestSupport;

namespace Madi.App.Tests;

public sealed class SteeringRuntimeTests
{
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task InputDuringCommitWaitsForDurabilityAndPreservesOrder()
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync(); var runtime = tab.Selected!;
        h.Store.DelayCommitSession = runtime.SessionId;
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, Guid.NewGuid(), "first");
        await Wait(h.Agent.Run("first").Started.Task);
        h.Agent.Run("first").Release.SetResult();
        await Wait(h.Store.CommitStarted.Task);
        var id = Guid.NewGuid();
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, id, "second");
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, id, "second");
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, Guid.NewGuid(), "third");
        Assert.False(h.Agent.Run("second").Started.Task.IsCompleted);
        Assert.True(runtime.IsRunning);
        h.Store.ReleaseCommit.SetResult();
        await Wait(h.Agent.Run("second").Started.Task);
        Assert.False(h.Agent.Run("third").Started.Task.IsCompleted);
        h.Agent.Run("second").Release.SetResult();
        await Wait(h.Agent.Run("third").Started.Task);
        h.Agent.Run("third").Release.SetResult();
        await Wait(runtime.Completion);
        var saved = await h.Store.LoadConversationAsync(runtime.SessionId);
        Assert.Equal(new[] { "first", "second", "third" }, saved!.Session.Conversation.OfType<UserMessage>().Select(x => x.Text));
        var statuses = runtime.EventsAfter(0).Select(e => e.Event).OfType<AppInputStatus>().ToArray();
        Assert.Equal(2, statuses.Count(x => x.State == InputDeliveryState.Pending));
        Assert.Equal(2, statuses.Count(x => x.State == InputDeliveryState.Included));
        Assert.Equal(new[] { InputDeliveryState.Pending, InputDeliveryState.Included }, statuses.Where(x => x.Input.Id == id).Select(x => x.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopKeepsPendingInputAndDoesNotResumeAutomatically(bool duringSave)
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync(); var runtime = tab.Selected!;
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, Guid.NewGuid(), "first");
        await Wait(h.Agent.Run("first").Started.Task);
        if (duringSave)
        {
            h.Store.DelayCommitSession = runtime.SessionId;
            h.Agent.Run("first").Release.SetResult();
            await Wait(h.Store.CommitStarted.Task);
        }
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, Guid.NewGuid(), "keep this");
        runtime.Cancel(); await Wait(runtime.Completion);
        Assert.False(h.Agent.Run("keep this").Started.Task.IsCompleted);
        Assert.Contains(runtime.EventsAfter(0), e => e.Event is AppInputStatus
            { State: InputDeliveryState.Interrupted, Input.Text: "keep this" });
        Assert.Empty((await h.Store.LoadConversationAsync(runtime.SessionId))!.Session.Conversation);
    }

    [Fact]
    public async Task InputTargetsCapturedSessionEvenAfterNavigation()
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync(); var runtime = tab.Selected!;
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, Guid.NewGuid(), "first");
        await Wait(h.Agent.Run("first").Started.Task);
        await h.Workspace.CreateSessionAsync(tab.Id, null);
        var other = tab.Selected!;
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, Guid.NewGuid(), "follow");
        h.Agent.Run("first").Release.SetResult();
        await Wait(h.Agent.Run("follow").Started.Task);
        h.Agent.Run("follow").Release.SetResult(); await Wait(runtime.Completion);
        Assert.Empty((await h.Store.LoadConversationAsync(other.SessionId))!.Session.Conversation);
        Assert.Equal(2, (await h.Store.LoadConversationAsync(runtime.SessionId))!.Session.Conversation.OfType<UserMessage>().Count());
    }

    [Fact]
    public async Task CommandsAndStaleRunIdsAreRejectedDuringExecution()
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync(); var runtime = tab.Selected!;
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, Guid.NewGuid(), "first");
        await Wait(h.Agent.Run("first").Started.Task);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Workspace.SubmitPromptAsync(
            tab.Id, runtime.SessionId, runtime.RunId, Guid.NewGuid(), "/new"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Workspace.SubmitPromptAsync(
            tab.Id, runtime.SessionId, Guid.NewGuid(), Guid.NewGuid(), "stale"));
        Assert.Same(runtime, tab.Selected);
        runtime.Cancel(); await Wait(runtime.Completion);
    }

    [Fact]
    public async Task NormalCompletedRunCanStartAgainInSameSession()
    {
        await using var h = new WorkspaceHarness();
        var tab = await h.Workspace.InitializeAsync(); var runtime = tab.Selected!;
        h.Agent.Run("first").Release.SetResult();
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, runtime.RunId, Guid.NewGuid(), "first");
        await Wait(runtime.Completion); var oldRun = runtime.RunId;
        h.Agent.Run("second").Release.SetResult();
        await h.Workspace.SubmitPromptAsync(tab.Id, runtime.SessionId, oldRun, Guid.NewGuid(), "second");
        await Wait(runtime.Completion);
        Assert.NotEqual(oldRun, runtime.RunId);
        Assert.Same(runtime, tab.Selected);
    }
}
