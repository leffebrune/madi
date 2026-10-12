using Madi.App;
using Madi.Core;
using Madi.GUI.Presentation;
using Madi.TestSupport;

namespace Madi.GUI.Tests;

public sealed class SteeringViewModelTests
{
    private sealed class Dispatcher : IUiDispatcher
    {
        private readonly object _gate = new();
        public Task InvokeAsync(Action action) { lock (_gate) action(); return Task.CompletedTask; }
    }

    [Fact]
    public async Task PendingInputDoesNotSplitStreamingAssistantAndAllUsersBecomePersisted()
    {
        await using var h = new WorkspaceHarness(); var tab = await h.Workspace.InitializeAsync();
        using var view = new WorkspaceTabViewModel(h.Workspace, tab, new Dispatcher());
        await view.SynchronizeAsync(); var vm = view.Current!;
        var input = new AgentInput(Guid.NewGuid(), "additional");
        await vm.ApplyExternalEventAsync(new AppUserInput("initial", tab.Selected!.SessionId, 0));
        await vm.ApplyExternalEventAsync(new AppAssistantDelta("one"));
        await vm.ApplyExternalEventAsync(new AppInputStatus(input, InputDeliveryState.Pending));
        await vm.ApplyExternalEventAsync(new AppAssistantDelta("two"));
        Assert.Equal("onetwo", Assert.Single(vm.Timeline, t => t.Kind == TimelineItemKind.Assistant).Text);
        Assert.Equal("additional", Assert.Single(vm.PendingInputs).Text);
        await vm.ApplyExternalEventAsync(new AppAssistantCompleted(TimeSpan.Zero, "onetwo"));
        await vm.ApplyExternalEventAsync(new AppInputStatus(input, InputDeliveryState.Included));
        Assert.Empty(vm.PendingInputs);
        await vm.ApplyExternalEventAsync(new AppUserInput(input.Text, tab.Selected.SessionId, 2, input.Id));
        await vm.ApplyExternalEventAsync(new AppAssistantDelta("new answer"));
        await vm.ApplyExternalEventAsync(new AppTurnCompleted(true, Persisted: true));
        Assert.Equal(2, vm.Timeline.Count(t => t.Kind == TimelineItemKind.Assistant));
        Assert.Empty(vm.PendingInputs);
        Assert.All(vm.Timeline.Where(t => t.Kind == TimelineItemKind.User), t => Assert.True(t.CanDeleteFromHere));
    }

    [Fact]
    public async Task SendDuringRunQueuesInsteadOfCancelingAndStopRemovesNotice()
    {
        await using var h = new WorkspaceHarness(); var tab = await h.Workspace.InitializeAsync();
        using var view = new WorkspaceTabViewModel(h.Workspace, tab, new Dispatcher());
        await view.SynchronizeAsync(); var vm = view.Current!;
        vm.Draft = "initial"; await vm.SendAsync();
        await h.Agent.Run("initial").Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Draft = "additional"; await vm.SendAsync();
        Assert.True(tab.Selected!.IsRunning);
        Assert.False(h.Agent.Run("initial").Cleaning.Task.IsCompleted);
        Assert.Equal(string.Empty, vm.Draft);
        Assert.Equal("additional", Assert.Single(vm.PendingInputs).Text);
        vm.Cancel(); await tab.Selected.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(vm.PendingInputs);
    }

    [Theory]
    [InlineData(InputDeliveryState.Included)]
    [InlineData(InputDeliveryState.Interrupted)]
    public async Task NonPendingStatusRemovesOnlyItsNoticeAndDoesNotRecreateIt(InputDeliveryState state)
    {
        await using var h = new WorkspaceHarness(); var tab = await h.Workspace.InitializeAsync();
        using var view = new WorkspaceTabViewModel(h.Workspace, tab, new Dispatcher());
        await view.SynchronizeAsync(); var vm = view.Current!;
        var first = new AgentInput(Guid.NewGuid(), "first");
        var second = new AgentInput(Guid.NewGuid(), "second");
        await vm.ApplyExternalEventAsync(new AppInputStatus(first, InputDeliveryState.Pending));
        await vm.ApplyExternalEventAsync(new AppInputStatus(first, InputDeliveryState.Pending));
        await vm.ApplyExternalEventAsync(new AppInputStatus(second, InputDeliveryState.Pending));
        Assert.Equal(2, vm.PendingInputs.Count);
        await vm.ApplyExternalEventAsync(new AppInputStatus(first, state));
        await vm.ApplyExternalEventAsync(new AppInputStatus(first, state));
        Assert.Equal(second.Id, Assert.Single(vm.PendingInputs).Id);
        // A normal turn completion must preserve input queued for the continuation turn.
        await vm.ApplyExternalEventAsync(new AppTurnCompleted(true, Persisted: true));
        Assert.Equal(second.Id, Assert.Single(vm.PendingInputs).Id);
        await vm.ApplyExternalEventAsync(new AppInputStatus(second, state));
        Assert.Empty(vm.PendingInputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledTurnClearsPendingNotices(bool canceled)
    {
        await using var h = new WorkspaceHarness(); var tab = await h.Workspace.InitializeAsync();
        using var view = new WorkspaceTabViewModel(h.Workspace, tab, new Dispatcher());
        await view.SynchronizeAsync(); var vm = view.Current!;
        var input = new AgentInput(Guid.NewGuid(), "additional");
        await vm.ApplyExternalEventAsync(new AppInputStatus(input, InputDeliveryState.Pending));
        await vm.ApplyExternalEventAsync(new AppTurnCompleted(false, Canceled: canceled));
        Assert.Empty(vm.PendingInputs);
        await vm.ApplyExternalEventAsync(new AppInputStatus(input, InputDeliveryState.Interrupted));
        Assert.Empty(vm.PendingInputs);
    }

    [Fact]
    public async Task RejectedCommandKeepsDraftAndDoesNotStopRun()
    {
        await using var h = new WorkspaceHarness(); var tab = await h.Workspace.InitializeAsync();
        using var view = new WorkspaceTabViewModel(h.Workspace, tab, new Dispatcher());
        await view.SynchronizeAsync(); var vm = view.Current!;
        vm.Draft = "initial"; await vm.SendAsync();
        await h.Agent.Run("initial").Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Draft = "/new"; await vm.SendAsync();
        Assert.Equal("/new", vm.Draft); Assert.True(vm.HasError); Assert.True(tab.Selected!.IsRunning);
        vm.Cancel(); await tab.Selected.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
