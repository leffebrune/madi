using Madi.App;
using Madi.Core;
using Madi.GUI.Presentation;
using Madi.Runtime.Authentication;

namespace Madi.GUI.Tests;

public sealed class TimelineViewModelTests
{
    [Fact]
    public async Task AppliesStreamingAndActivityEvents()
    {
        var viewModel = CreateViewModel();

        await viewModel.ApplyExternalEventAsync(new AppUserInput("질문"));
        await viewModel.ApplyExternalEventAsync(new AppAssistantDelta("답"));
        await viewModel.ApplyExternalEventAsync(new AppAssistantDelta("변"));
        await viewModel.ApplyExternalEventAsync(new AppToolStarted("call-1", "read", "{}"));
        await viewModel.ApplyExternalEventAsync(new AppToolCompleted(
            "call-1",
            "read",
            "{}",
            "결과",
            IsError: false));

        Assert.Equal("답변", viewModel.Timeline[1].Text);
        var tool = Assert.Single(viewModel.Timeline, item => item.Kind == TimelineItemKind.Tool);
        Assert.False(tool.IsRunning);
        Assert.Contains("결과", tool.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationLoadReplacesTimelineAndPreservesActivities()
    {
        var viewModel = CreateViewModel();
        var snapshot = new ConversationSnapshot(
            new SessionSnapshot("session-1", "복원", null),
            [
                new ConversationEntrySnapshot(ConversationEntryKind.User, "과거 질문"),
                new ConversationEntrySnapshot(
                    ConversationEntryKind.Tool,
                    "read",
                    "결과",
                    "call-1",
                    IsError: true),
            ]);

        await viewModel.ApplyExternalEventAsync(new AppConversationLoaded(snapshot));

        Assert.Equal("복원", viewModel.HeaderTitle);
        Assert.Equal(2, viewModel.Timeline.Count);
        Assert.True(viewModel.Timeline[1].IsError);
    }

    [Fact]
    public async Task ConversationClearedRemovesDeletedTimeline()
    {
        var viewModel = CreateViewModel();
        await viewModel.ApplyExternalEventAsync(new AppUserInput("삭제할 질문"));

        await viewModel.ApplyExternalEventAsync(new AppConversationCleared());

        Assert.Empty(viewModel.Timeline);
    }

    private static MainViewModel CreateViewModel()
    {
        var controller = new MadiController(
            new StubAgent(),
            new StubStore(),
            new ModelOptions("first"),
            [new AvailableModel
            {
                Name = "first",
                Provider = "test",
                Api = "test",
                MaxContextTokens = 1000,
            }],
            new StubAuthentication(),
            "Helpful assistant.",
            "C:\\work");
        return new MainViewModel(controller, new ImmediateUiDispatcher());
    }

    private sealed class StubAgent : IAgent
    {
        public async IAsyncEnumerable<AgentEvent> RespondAsync(
            Work? work,
            Session session,
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubStore : IWorkSessionStore
    {
        public Task<IReadOnlyList<Work>> LoadWorksAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Work>>([]);
        public Task<IReadOnlyList<Session>> LoadSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Session>>([]);
        public Task<CurrentConversation?> LoadCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<CurrentConversation?>(null);
        public Task<CurrentConversation?> LoadConversationAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<CurrentConversation?>(null);
        public Task SaveWorkAsync(Work work, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveSessionAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendConversationAsync(Session session, int startIndex, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SetCurrentAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubAuthentication : IOAuthAuthentication
    {
        public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default) => Task.FromResult("token");
        public Task LoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
