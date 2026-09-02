using Madi.App;
using Madi.Core;
using Madi.GUI.Presentation;
using Madi.Runtime.Authentication;

namespace Madi.GUI.Tests;

public sealed class TimelineViewModelTests
{
    [Fact]
    public void DefersActivityMarkdownUntilExpanded()
    {
        var activity = new TimelineItemViewModel(
            TimelineItemKind.Tool,
            "read",
            "long tool details");

        Assert.Empty(activity.RenderedText);
        Assert.Empty(activity.ActivityDetailsMarkdown);
        Assert.Empty(activity.StandaloneDetailsMarkdown);

        activity.IsActivityExpanded = true;

        Assert.Equal("long tool details", activity.ActivityDetailsMarkdown);
        Assert.Empty(activity.StandaloneDetailsMarkdown);

        activity.IsActivityExpanded = false;

        Assert.Empty(activity.ActivityDetailsMarkdown);
    }

    [Fact]
    public void DefersStandaloneMarkdownUntilExpanded()
    {
        var information = new TimelineItemViewModel(
            TimelineItemKind.Information,
            "summary",
            "standalone details");

        Assert.Equal("summary", information.RenderedText);
        Assert.Empty(information.StandaloneDetailsMarkdown);

        information.IsStandaloneDetailsExpanded = true;

        Assert.Equal("standalone details", information.StandaloneDetailsMarkdown);
    }

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
        Assert.Equal("1.25초", viewModel.Timeline[1].TimingText);
        var tool = Assert.Single(viewModel.Timeline, item => item.Kind == TimelineItemKind.Tool);
        Assert.False(tool.IsRunning);
        Assert.Equal("240ms", tool.TimingText);
        Assert.Equal("file.txt", tool.Description);
        Assert.Contains("결과", tool.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamsCompactToolsIntoTheirParentSubagent()
    {
        var viewModel = CreateViewModel();

        await viewModel.ApplyExternalEventAsync(new AppSubagentStarted(
            "subagent-1",
            "reviewer",
            "검토해라"));
        await viewModel.ApplyExternalEventAsync(new AppSubagentToolStarted(
            "subagent-1",
            "tool-1",
            "read",
            "{\"path\":\"src/Main.cs\"}"));

        var subagent = Assert.Single(
            viewModel.Timeline,
            item => item.Kind == TimelineItemKind.Subagent);
        var tool = Assert.Single(subagent.ChildTools);
        Assert.True(tool.IsRunning);
        Assert.Equal("read", tool.Name);
        Assert.Equal("src/Main.cs", tool.Description);

        await viewModel.ApplyExternalEventAsync(new AppSubagentToolCompleted(
            "subagent-1",
            "tool-1",
            "read",
            IsError: false,
            ToolDuration: TimeSpan.FromMilliseconds(75)));

        Assert.False(tool.IsRunning);
        Assert.False(tool.IsError);
        Assert.Equal("75ms", tool.TimingText);
    }

    [Fact]
    public async Task ChildToolIdsAreScopedToTheirParentSubagent()
    {
        var viewModel = CreateViewModel();
        await viewModel.ApplyExternalEventAsync(new AppSubagentStarted("sub-1", "reviewer", "첫째"));
        await viewModel.ApplyExternalEventAsync(new AppSubagentStarted("sub-2", "scanner", "둘째"));
        await viewModel.ApplyExternalEventAsync(new AppSubagentToolStarted("sub-1", "tool-1", "read", "{}"));
        await viewModel.ApplyExternalEventAsync(new AppSubagentToolStarted("sub-2", "tool-1", "grep", "{}"));

        await viewModel.ApplyExternalEventAsync(new AppSubagentToolCompleted(
            "sub-2",
            "tool-1",
            "grep",
            IsError: true,
            ToolDuration: TimeSpan.FromMilliseconds(10)));

        Assert.True(viewModel.Timeline[0].ChildTools[0].IsRunning);
        Assert.False(viewModel.Timeline[1].ChildTools[0].IsRunning);
        Assert.True(viewModel.Timeline[1].ChildTools[0].IsError);
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
        Assert.Equal("500ms", viewModel.Timeline[^1].TimingText);
        Assert.Null(viewModel.Timeline[0].ApiDuration);
    }

    [Fact]
    public async Task EmptyCompletionCreatesAssistantItemForApiTiming()
    {
        var viewModel = CreateViewModel();

        await viewModel.ApplyExternalEventAsync(new AppUserInput("빈 응답"));
        await viewModel.ApplyExternalEventAsync(new AppAssistantCompleted(
            TimeSpan.FromMilliseconds(200)));

        var assistant = Assert.IsType<TimelineItemViewModel>(viewModel.Timeline[^1]);
        Assert.Equal(TimelineItemKind.Assistant, assistant.Kind);
        Assert.Empty(assistant.Text);
        Assert.Equal("200ms", assistant.TimingText);
    }

    [Fact]
    public async Task ToolOnlyResponseDisplaysToolCallSummaryBeforeActivities()
    {
        var viewModel = CreateViewModel();

        await viewModel.ApplyExternalEventAsync(new AppUserInput("파일 읽기"));
        await viewModel.ApplyExternalEventAsync(new AppAssistantCompleted(
            TimeSpan.FromMilliseconds(120),
            "도구 호출 : read, bash"));
        await viewModel.ApplyExternalEventAsync(new AppToolStarted("read-1", "read", "{}"));
        await viewModel.ApplyExternalEventAsync(new AppToolStarted("bash-1", "bash", "{}"));

        Assert.Equal("도구 호출 : read, bash", viewModel.Timeline[1].Text);
        Assert.Equal("120ms", viewModel.Timeline[1].TimingText);
        Assert.Equal(TimelineItemKind.Tool, viewModel.Timeline[2].Kind);
        Assert.Equal(TimelineItemKind.Tool, viewModel.Timeline[3].Kind);
    }

    [Fact]
    public async Task CompletionReplacesWhitespaceOnlyStreamedTextWithToolCallSummary()
    {
        var viewModel = CreateViewModel();

        await viewModel.ApplyExternalEventAsync(new AppAssistantDelta("  "));
        await viewModel.ApplyExternalEventAsync(new AppAssistantCompleted(
            TimeSpan.FromMilliseconds(120),
            "도구 호출 : read"));

        Assert.Equal("도구 호출 : read", viewModel.Timeline[^1].Text);
        Assert.Equal("120ms", viewModel.Timeline[^1].TimingText);
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
                new ConversationEntrySnapshot(
                    ConversationEntryKind.User,
                    "과거 질문",
                    ConversationIndex: 3),
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
        Assert.True(viewModel.Timeline[0].CanDeleteFromHere);
        Assert.Equal("session-1", viewModel.Timeline[0].SessionId);
        Assert.Equal(3, viewModel.Timeline[0].ConversationIndex);
        Assert.True(viewModel.Timeline[1].IsError);
        Assert.Equal(
            $"src/Main.cs{Environment.NewLine}offset 5 · limit 10",
            viewModel.Timeline[1].Description);
    }

    [Fact]
    public async Task ReasoningEffortOptionsAreFixedAndUpdateSelectedValue()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        Assert.Equal(["instant", "low", "medium", "high"],
            viewModel.ReasoningEfforts.Select(item => item.Key));
        Assert.Equal("medium", viewModel.SelectedReasoningEffortKey);
        Assert.True(viewModel.CanSelectReasoningEffort);

        await viewModel.SetReasoningEffortAsync("instant");

        Assert.Equal("instant", viewModel.SelectedReasoningEffortKey);
    }

    [Fact]
    public async Task SessionChangeReassertsModelAndReasoningSelectorValues()
    {
        var viewModel = CreateViewModel();
        var model = new ModelOptions("first", ReasoningEffort.Medium, "test");

        await viewModel.ApplyExternalEventAsync(new AppStateChanged(new AppState(
            model,
            Work: null,
            new SessionSnapshot("session-1", "첫 대화", WorkId: null),
            AppRunState.Idle,
            AuthenticationKind.None,
            AuthenticationState.NotRequired,
            "C:\\work")));

        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        await viewModel.ApplyExternalEventAsync(new AppStateChanged(new AppState(
            model,
            Work: null,
            new SessionSnapshot("session-2", "둘째 대화", WorkId: null),
            AppRunState.Idle,
            AuthenticationKind.None,
            AuthenticationState.NotRequired,
            "C:\\work")));

        Assert.Contains(nameof(MainViewModel.SelectedModelName), changedProperties);
        Assert.Contains(nameof(MainViewModel.SelectedReasoningEffortKey), changedProperties);
        Assert.Equal("test/first", viewModel.SelectedModelName);
        Assert.Equal("medium", viewModel.SelectedReasoningEffortKey);
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
            new ModelOptions("first", ReasoningEffort.Medium),
            [new AvailableModel
            {
                Name = "first",
                Provider = "test",
                Api = "test",
                SupportsReasoning = true,
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
        public Task RewriteConversationAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
