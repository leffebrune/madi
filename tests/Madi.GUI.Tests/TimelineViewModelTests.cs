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
        await viewModel.ApplyExternalEventAsync(new AppAssistantCompleted(
            TimeSpan.FromMilliseconds(1250)));
        await viewModel.ApplyExternalEventAsync(new AppToolStarted(
            "call-1",
            "read",
            """{"path":"file.txt"}"""));
        await viewModel.ApplyExternalEventAsync(new AppToolCompleted(
            "call-1",
            "read",
            """{"path":"file.txt"}""",
            "결과",
            IsError: false,
            ToolDuration: TimeSpan.FromMilliseconds(240),
            ApiDuration: TimeSpan.FromMilliseconds(1500)));

        Assert.Equal("답변", viewModel.Timeline[1].Text);
        Assert.Equal("API 1.25초", viewModel.Timeline[1].TimingText);
        var tool = Assert.Single(viewModel.Timeline, item => item.Kind == TimelineItemKind.Tool);
        Assert.False(tool.IsRunning);
        Assert.Equal("도구 240ms · API 1.5초", tool.TimingText);
        Assert.Equal("file.txt", tool.Description);
        Assert.Contains("결과", tool.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionCreatesAssistantWhenProviderDidNotStreamDeltas()
    {
        var viewModel = CreateViewModel();
        await viewModel.ApplyExternalEventAsync(new AppAssistantDelta("이전 답변"));
        await viewModel.ApplyExternalEventAsync(new AppUserInput("새 질문"));

        await viewModel.ApplyExternalEventAsync(new AppAssistantCompleted(
            TimeSpan.FromMilliseconds(500),
            "새 답변"));

        Assert.Equal(TimelineItemKind.Assistant, viewModel.Timeline[^1].Kind);
        Assert.Equal("새 답변", viewModel.Timeline[^1].Text);
        Assert.Equal("API 500ms", viewModel.Timeline[^1].TimingText);
        Assert.Null(viewModel.Timeline[0].ApiDuration);
    }

    [Fact]
    public async Task StreamingDoesNotReenableFollowLatestAfterUserScrollsAway()
    {
        var viewModel = CreateViewModel();
        viewModel.FollowLatest = false;

        await viewModel.ApplyExternalEventAsync(new AppAssistantDelta("답변"));

        Assert.False(viewModel.FollowLatest);
    }

    [Fact]
    public async Task NewUserInputReenablesFollowLatest()
    {
        var viewModel = CreateViewModel();
        viewModel.FollowLatest = false;

        await viewModel.ApplyExternalEventAsync(new AppUserInput("새 질문"));

        Assert.True(viewModel.FollowLatest);
    }

    [Fact]
    public async Task WorkPromptDraftSurvivesUnrelatedBusyStateChanges()
    {
        var viewModel = CreateViewModel();
        var work = new WorkSnapshot("work-1", "Work", "저장된 프롬프트");

        await viewModel.ApplyExternalEventAsync(new AppStateChanged(new AppState(
            new ModelOptions("first"),
            work,
            new SessionSnapshot("session-1", "대화", work.Id),
            AppRunState.Idle,
            AuthenticationKind.OAuth,
            AuthenticationState.Ready,
            "C:\\work")));
        viewModel.WorkPromptDraft = "수정 중인 프롬프트";

        await viewModel.ApplyExternalEventAsync(new AppStateChanged(new AppState(
            new ModelOptions("first"),
            work,
            new SessionSnapshot("session-1", "대화", work.Id),
            AppRunState.RunningPrompt,
            AuthenticationKind.OAuth,
            AuthenticationState.Ready,
            "C:\\work")));

        Assert.Equal("수정 중인 프롬프트", viewModel.WorkPromptDraft);
        Assert.False(viewModel.CanSaveWorkPrompt);
    }

    [Fact]
    public async Task UsageUpdatesStatusWithoutAddingTimelineItem()
    {
        var viewModel = CreateViewModel();

        await viewModel.ApplyExternalEventAsync(new AppUsageReported(new ModelUsage(
            InputTokens: 120,
            CachedTokens: 80,
            CacheWriteTokens: null,
            OutputTokens: 30,
            ReasoningTokens: 10,
            TotalTokens: 150)));

        Assert.Empty(viewModel.Timeline);
        Assert.Equal(
            "입력 120 · 캐시 읽기 80 · 캐시 쓰기 - · 출력 30 · 추론 10 · 합계 150",
            viewModel.UsageText);
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
                    IsError: true,
                    ActivityInput: """{"path":"src/Main.cs","offset":5,"limit":10}"""),
            ]);

        await viewModel.ApplyExternalEventAsync(new AppConversationLoaded(snapshot));

        Assert.Equal("복원", viewModel.HeaderTitle);
        Assert.Equal(2, viewModel.Timeline.Count);
        Assert.True(viewModel.Timeline[1].IsError);
        Assert.Equal(
            $"src/Main.cs{Environment.NewLine}offset 5 · limit 10",
            viewModel.Timeline[1].Description);
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
            "C:\\work");
        return new MainViewModel(controller, new ImmediateUiDispatcher());
    }

    private sealed class StubAgent : IAgent
    {
        public string SystemInstructions { get; set; } = "Helpful assistant.";

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
        public Task<bool> DeleteWorkAsync(string workId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SetCurrentAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubAuthentication : IOAuthAuthentication
    {
        public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default) => Task.FromResult("token");
        public Task LoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
