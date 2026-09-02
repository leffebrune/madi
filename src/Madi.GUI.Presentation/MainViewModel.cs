using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Madi.App;
using Madi.Core;

namespace Madi.GUI.Presentation;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly MadiController _controller;
    private readonly IUiDispatcher _dispatcher;
    private readonly IActivityDescriptionFormatter _activityDescriptionFormatter;
    private CancellationTokenSource? _operationCancellation;
    private string _draft = string.Empty;
    private string _headerTitle = "새 대화";
    private string _workingDirectory;
    private string _authenticationLabel = "확인 중";
    private string? _selectedModelName;
    private string? _selectedReasoningEffortKey;
    private string? _currentWorkId;
    private string _currentWorkPrompt = string.Empty;
    private string _workPromptDraft = string.Empty;
    private string _usageText = "사용량 정보 없음";
    private string? _errorText;
    private bool _isBusy;
    private bool _isSignedIn;
    private bool _supportsOAuth;
    private string? _submittedDraft;
    private TimelineItemViewModel? _pendingUserTimelineItem;

    public MainViewModel(
        MadiController controller,
        IUiDispatcher dispatcher,
        IActivityDescriptionFormatter? activityDescriptionFormatter = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _activityDescriptionFormatter = activityDescriptionFormatter ?? new ActivityDescriptionFormatter();
        _workingDirectory = controller.State.WorkingDirectory;
        foreach (var model in controller.AvailableModels)
        {
            Models.Add(new ModelItemViewModel(
                model.Name,
                model.Provider,
                model.MaxContextTokens,
                model.SupportsReasoning));
        }

        ReasoningEfforts.Add(new ReasoningEffortItemViewModel("instant", "즉시", null));
        ReasoningEfforts.Add(new ReasoningEffortItemViewModel("low", "낮음", ReasoningEffort.Low));
        ReasoningEfforts.Add(new ReasoningEffortItemViewModel("medium", "중간", ReasoningEffort.Medium));
        ReasoningEfforts.Add(new ReasoningEffortItemViewModel("high", "높음", ReasoningEffort.High));
    }

    public event EventHandler? ExitRequested;

    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = [];
    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; } = [];
    public ObservableCollection<SuggestionItemViewModel> Suggestions { get; } = [];
    public ObservableCollection<ModelItemViewModel> Models { get; } = [];
    public ObservableCollection<ReasoningEffortItemViewModel> ReasoningEfforts { get; } = [];

    public int LegacyItemCount { get; private set; }

    public string SystemInstructions => _controller.SystemInstructions;

    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value))
            {
                _ = RefreshSuggestionsAsync(value);
            }
        }
    }

    public string HeaderTitle
    {
        get => _headerTitle;
        private set => SetProperty(ref _headerTitle, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        private set => SetProperty(ref _workingDirectory, value);
    }

    public string AuthenticationLabel
    {
        get => _authenticationLabel;
        private set => SetProperty(ref _authenticationLabel, value);
    }

    public string? SelectedModelName
    {
        get => _selectedModelName;
        private set
        {
            // Reassert the controller value even when the backing value is unchanged.
            // A OneWay ComboBox can have a stale local selection after a rejected action.
            if (!SetProperty(ref _selectedModelName, value))
            {
                OnPropertyChanged();
            }
            OnPropertyChanged(nameof(CanSelectReasoningEffort));
        }
    }

    public string? SelectedReasoningEffortKey
    {
        get => _selectedReasoningEffortKey;
        private set
        {
            // Keep the selector synchronized for the same reason as SelectedModelName.
            if (!SetProperty(ref _selectedReasoningEffortKey, value))
            {
                OnPropertyChanged();
            }
        }
    }

    public string? CurrentWorkId
    {
        get => _currentWorkId;
        private set
        {
            if (SetProperty(ref _currentWorkId, value))
            {
                OnPropertyChanged(nameof(HasCurrentWork));
                OnPropertyChanged(nameof(CanSaveWorkPrompt));
            }
        }
    }

    public string CurrentWorkPrompt
    {
        get => _currentWorkPrompt;
        private set
        {
            if (SetProperty(ref _currentWorkPrompt, value))
            {
                OnPropertyChanged(nameof(CanSaveWorkPrompt));
            }
        }
    }

    public string WorkPromptDraft
    {
        get => _workPromptDraft;
        set
        {
            if (SetProperty(ref _workPromptDraft, value))
            {
                OnPropertyChanged(nameof(CanSaveWorkPrompt));
            }
        }
    }

    public bool HasCurrentWork => CurrentWorkId is not null;
    public bool CanSaveWorkPrompt => HasCurrentWork && CanInteract && IsWorkPromptDirty;

    private bool IsWorkPromptDirty =>
        !string.Equals(WorkPromptDraft, CurrentWorkPrompt, StringComparison.Ordinal);

    public string UsageText
    {
        get => _usageText;
        private set => SetProperty(ref _usageText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanInteract));
                OnPropertyChanged(nameof(CanSaveWorkPrompt));
                OnPropertyChanged(nameof(SendButtonText));
                OnPropertyChanged(nameof(CanToggleAuthentication));
                OnPropertyChanged(nameof(CanSelectReasoningEffort));
                foreach (var item in Timeline)
                {
                    item.SetConversationInteraction(CanInteract);
                }
            }
        }
    }

    public bool CanInteract => !IsBusy;
    public bool CanToggleAuthentication => CanInteract && _supportsOAuth;
    public bool CanSelectReasoningEffort => CanInteract
        && Models.FirstOrDefault(model => string.Equals(
            model.Key,
            SelectedModelName,
            StringComparison.OrdinalIgnoreCase))?.SupportsReasoning == true;
    public string SendButtonText => IsBusy ? "취소" : "전송";

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set => SetProperty(ref _isSignedIn, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _controller.InitializeAsync(cancellationToken);
        await _dispatcher.InvokeAsync(() => ApplyState(_controller.State));
        var navigation = await _controller.GetNavigationAsync(cancellationToken);
        await _dispatcher.InvokeAsync(() => ApplyNavigation(navigation));
        if (_controller.GetCurrentConversation() is { } conversation)
        {
            await _dispatcher.InvokeAsync(() => ApplyConversation(conversation));
        }
    }

    public Task SendOrCancelAsync()
    {
        if (IsBusy)
        {
            Cancel();
            return Task.CompletedTask;
        }

        var text = Draft.Trim();
        _submittedDraft = text;
        return text.Length == 0
            ? Task.CompletedTask
            : ExecuteAsync(new SubmitPromptAction(text));
    }

    public Task SelectModelAsync(string model) =>
        string.Equals(model, SelectedModelName, StringComparison.OrdinalIgnoreCase)
            ? Task.CompletedTask
            : ExecuteAsync(new SelectModelAction(model));

    public Task SetReasoningEffortAsync(string key)
    {
        var item = ReasoningEfforts.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        return item is null || string.Equals(
                item.Key,
                SelectedReasoningEffortKey,
                StringComparison.OrdinalIgnoreCase)
            ? Task.CompletedTask
            : ExecuteAsync(new SetReasoningEffortAction(item.Value));
    }

    public Task CreateSessionAsync(string? workId = null) =>
        ExecuteAsync(new CreateSessionAction(workId));

    public Task CreateWorkAsync(string title) =>
        ExecuteAsync(new CreateWorkAction(title));

    public Task SaveWorkPromptAsync()
    {
        if (!CanSaveWorkPrompt)
        {
            return Task.CompletedTask;
        }

        var prompt = WorkPromptDraft.Trim();
        WorkPromptDraft = prompt;
        return ExecuteAsync(new UpdateWorkPromptAction(prompt));
    }

    public Task ActivateSessionAsync(string sessionId) =>
        ExecuteAsync(new ActivateSessionAction(sessionId));

    public Task DeleteSessionAsync(string sessionId) =>
        ExecuteAsync(new DeleteSessionAction(sessionId));

    public Task DeleteConversationFromAsync(string sessionId, int conversationIndex) =>
        !CanInteract
            ? Task.CompletedTask
            : ExecuteAsync(new DeleteConversationFromAction(sessionId, conversationIndex));

    public Task DeleteWorkAsync(string workId) =>
        ExecuteAsync(new DeleteWorkAction(workId));

    public Task ToggleAuthenticationAsync() =>
        !CanToggleAuthentication
            ? Task.CompletedTask
            : ExecuteAsync(IsSignedIn ? new LogoutAction() : new LoginAction());

    public Task ImportLegacyDataAsync() =>
        ExecuteAsync(new ImportLegacyWorkspaceDataAction());

    public async Task ApplyExternalEventAsync(AppEvent item) =>
        await _dispatcher.InvokeAsync(() => ApplyEvent(item));

    public void CompleteSuggestion(SuggestionItemViewModel suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        Draft = suggestion.CompletionText;
        Suggestions.Clear();
    }

    public void ClearError() => ErrorText = null;

    public void Cancel() => _operationCancellation?.Cancel();

    public void Dispose()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private async Task ExecuteAsync(AppAction action)
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        try
        {
            var deltaBuffer = new StringBuilder();
            var lastDeltaFlush = Stopwatch.StartNew();
            await foreach (var item in _controller.ExecuteAsync(action, cancellation.Token))
            {
                if (item is AppAssistantDelta delta)
                {
                    deltaBuffer.Append(delta.Text);
                    if (lastDeltaFlush.ElapsedMilliseconds < 40)
                    {
                        continue;
                    }

                    await FlushDeltaAsync(deltaBuffer);
                    lastDeltaFlush.Restart();
                    continue;
                }

                await FlushDeltaAsync(deltaBuffer);
                await _dispatcher.InvokeAsync(() => ApplyEvent(item));
            }
            await FlushDeltaAsync(deltaBuffer);
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
            {
                _operationCancellation = null;
            }
        }
    }

    private void ApplyEvent(AppEvent item)
    {
        switch (item)
        {
            case AppStateChanged state:
                ApplyState(state.State);
                break;
            case AppNavigationChanged navigation:
                ApplyNavigation(navigation.Navigation);
                break;
            case AppConversationLoaded conversation:
                _pendingUserTimelineItem = null;
                ApplyConversation(conversation.Conversation);
                break;
            case AppConversationCleared:
                _pendingUserTimelineItem = null;
                Timeline.Clear();
                UsageText = "사용량 정보 없음";
                break;
            case AppUserInput user:
                _pendingUserTimelineItem = new TimelineItemViewModel(
                    TimelineItemKind.User,
                    user.Text,
                    sessionId: user.SessionId,
                    conversationIndex: user.ConversationIndex);
                _pendingUserTimelineItem.SetConversationInteraction(CanInteract);
                Timeline.Add(_pendingUserTimelineItem);
                UsageText = "응답 사용량 계산 중…";
                Draft = string.Empty;
                Suggestions.Clear();
                break;
            case AppTurnCompleted completed:
                if (completed.Succeeded)
                {
                    _submittedDraft = null;
                    _pendingUserTimelineItem?.MarkConversationItemPersisted();
                }
                else
                {
                    StopRunningChildTools(markAsError: !completed.Canceled);
                    if (Draft.Length == 0 && _submittedDraft is not null)
                    {
                        Draft = _submittedDraft;
                        _submittedDraft = null;
                    }
                }
                _pendingUserTimelineItem = null;
                break;
            case AppAssistantDelta delta:
                var assistant = Timeline.LastOrDefault(static entry =>
                    entry.Kind == TimelineItemKind.Assistant && entry.ActivityId is null);
                if (assistant is null || !ReferenceEquals(Timeline.LastOrDefault(), assistant))
                {
                    assistant = new TimelineItemViewModel(TimelineItemKind.Assistant, string.Empty);
                    Timeline.Add(assistant);
                }
                assistant.Text += delta.Text;
                break;
            case AppAssistantCompleted completed:
                var completedAssistant = Timeline.LastOrDefault();
                if (completedAssistant?.Kind != TimelineItemKind.Assistant)
                {
                    completedAssistant = new TimelineItemViewModel(
                        TimelineItemKind.Assistant,
                        completed.Text);
                    Timeline.Add(completedAssistant);
                }
                else if (string.IsNullOrWhiteSpace(completedAssistant.Text)
                    && !string.IsNullOrWhiteSpace(completed.Text))
                {
                    completedAssistant.Text = completed.Text;
                }
                completedAssistant.ApiDuration = completed.ApiDuration > TimeSpan.Zero
                    ? completed.ApiDuration
                    : null;
                break;
            case AppToolStarted tool:
                Timeline.Add(new TimelineItemViewModel(
                    TimelineItemKind.Tool,
                    tool.Name,
                    $"### 인자\n```json\n{tool.Arguments}\n```",
                    tool.CallId,
                    isRunning: true,
                    description: _activityDescriptionFormatter.FormatTool(tool.Name, tool.Arguments)));
                break;
            case AppToolCompleted tool:
                CompleteActivity(
                    tool.CallId,
                    tool.Name,
                    ActivityDetailsFormatter.FormatTool(tool.Arguments, tool.Result),
                    tool.IsError,
                    tool.ToolDuration);
                break;
            case AppSubagentStarted subagent:
                Timeline.Add(new TimelineItemViewModel(
                    TimelineItemKind.Subagent,
                    subagent.Name,
                    $"### 프롬프트\n{subagent.Prompt}",
                    subagent.CallId,
                    isRunning: true,
                    description: _activityDescriptionFormatter.FormatSubagent(
                        subagent.Name,
                        subagent.Prompt)));
                break;
            case AppSubagentCompleted subagent:
                CompleteActivity(
                    subagent.CallId,
                    subagent.Name,
                    ActivityDetailsFormatter.FormatSubagent(subagent.Prompt, subagent.Response),
                    subagent.IsError,
                    subagent.ToolDuration);
                break;
            case AppSubagentToolStarted tool:
                AddSubagentTool(tool);
                break;
            case AppSubagentToolCompleted tool:
                CompleteSubagentTool(tool);
                break;
            case AppUsageReported usage:
                UsageText = FormatUsage(usage.Usage);
                return;
            case AppMessage message:
                var kind = message.Kind switch
                {
                    AppMessageKind.Warning => TimelineItemKind.Warning,
                    AppMessageKind.Error => TimelineItemKind.Error,
                    _ => TimelineItemKind.Information,
                };
                Timeline.Add(new TimelineItemViewModel(kind, message.Text));
                if (message.Kind == AppMessageKind.Error)
                {
                    ErrorText = message.Text;
                }
                break;
            case AppAuthenticationProgress authentication:
                Timeline.Add(new TimelineItemViewModel(
                    authentication.Kind == AuthenticationProgressKind.Failed
                        ? TimelineItemKind.Error
                        : TimelineItemKind.Information,
                    authentication.Message));
                if (authentication.Kind == AuthenticationProgressKind.Failed)
                {
                    ErrorText = authentication.Message;
                }
                break;
            case AppExitRequested:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }

    }

    private void ApplyState(AppState state)
    {
        IsBusy = state.IsBusy;
        SelectedModelName = state.Model.QualifiedName;
        SelectedReasoningEffortKey = state.Model.ReasoningEffort switch
        {
            null => "instant",
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            _ => null,
        };

        var nextWorkId = state.Work?.Id;
        var workChanged = !string.Equals(CurrentWorkId, nextWorkId, StringComparison.Ordinal);
        CurrentWorkId = nextWorkId;
        CurrentWorkPrompt = state.Work?.Prompt ?? string.Empty;
        if (workChanged || !IsWorkPromptDirty)
        {
            WorkPromptDraft = CurrentWorkPrompt;
        }
        HeaderTitle = state.Session?.Title ?? "새 대화";
        WorkingDirectory = state.WorkingDirectory;
        _supportsOAuth = state.AuthenticationKind == AuthenticationKind.OAuth;
        OnPropertyChanged(nameof(CanToggleAuthentication));
        IsSignedIn = _supportsOAuth && state.Authentication == AuthenticationState.Ready;
        AuthenticationLabel = (state.AuthenticationKind, state.Authentication) switch
        {
            (AuthenticationKind.OAuth, AuthenticationState.Ready) => "로그인됨",
            (AuthenticationKind.OAuth, AuthenticationState.ActionRequired) => "로그인 필요",
            (AuthenticationKind.ApiKey, AuthenticationState.Ready) => "API 키 설정됨",
            (AuthenticationKind.ApiKey, AuthenticationState.ActionRequired) => "API 키 필요",
            (_, AuthenticationState.NotRequired) => "인증 불필요",
            _ => "확인 중",
        };
    }

    private void ApplyNavigation(NavigationSnapshot navigation)
    {
        LegacyItemCount = navigation.LegacyItemCount;
        OnPropertyChanged(nameof(LegacyItemCount));
        NavigationItems.Clear();
        foreach (var work in navigation.Works)
        {
            NavigationItems.Add(new NavigationItemViewModel(
                work.Title,
                SessionId: null,
                work.Id,
                0,
                IsHeader: true));
            foreach (var session in work.Sessions)
            {
                NavigationItems.Add(new NavigationItemViewModel(
                    session.Title,
                    session.Id,
                    work.Id,
                    1,
                    IsHeader: false,
                    string.Equals(session.Id, _controller.State.Session?.Id, StringComparison.Ordinal)));
            }
        }

        if (navigation.StandaloneSessions.Count > 0)
        {
            NavigationItems.Add(new NavigationItemViewModel(
                "독립 대화",
                SessionId: null,
                WorkId: null,
                0,
                IsHeader: true));
            foreach (var session in navigation.StandaloneSessions)
            {
                NavigationItems.Add(new NavigationItemViewModel(
                    session.Title,
                    session.Id,
                    WorkId: null,
                    1,
                    IsHeader: false,
                    string.Equals(session.Id, _controller.State.Session?.Id, StringComparison.Ordinal)));
            }
        }
    }

    private void ApplyConversation(ConversationSnapshot conversation)
    {
        Timeline.Clear();
        UsageText = "사용량 정보 없음";
        foreach (var entry in conversation.Entries)
        {
            Timeline.Add(new TimelineItemViewModel(
                entry.Kind switch
                {
                    ConversationEntryKind.User => TimelineItemKind.User,
                    ConversationEntryKind.Assistant => TimelineItemKind.Assistant,
                    ConversationEntryKind.Tool => TimelineItemKind.Tool,
                    ConversationEntryKind.Subagent => TimelineItemKind.Subagent,
                    _ => TimelineItemKind.Information,
                },
                entry.ActivityName ?? entry.Summary,
                entry.Details,
                entry.ActivityId,
                isError: entry.IsError,
                description: GetActivityDescription(entry),
                sessionId: conversation.Session.Id,
                conversationIndex: entry.ConversationIndex,
                isPersistedConversationItem: true));
            Timeline[^1].SetConversationInteraction(CanInteract);
        }
        HeaderTitle = conversation.Session.Title;
    }

    private string? GetActivityDescription(ConversationEntrySnapshot entry) => entry.Kind switch
    {
        ConversationEntryKind.Tool when entry.ActivityInput is not null =>
            _activityDescriptionFormatter.FormatTool(
                entry.ActivityName ?? entry.Summary,
                entry.ActivityInput),
        ConversationEntryKind.Subagent when entry.ActivityInput is not null =>
            _activityDescriptionFormatter.FormatSubagent(
                entry.ActivityName ?? entry.Summary,
                entry.ActivityInput),
        _ => null,
    };

    private void CompleteActivity(
        string callId,
        string text,
        string details,
        bool isError,
        TimeSpan toolDuration)
    {
        var activity = Timeline.LastOrDefault(entry =>
            string.Equals(entry.ActivityId, callId, StringComparison.Ordinal));
        if (activity is null)
        {
            return;
        }

        activity.Text = text;
        activity.Details = details;
        activity.IsRunning = false;
        activity.IsError = isError;
        activity.ToolDuration = toolDuration > TimeSpan.Zero ? toolDuration : null;
        foreach (var childTool in activity.ChildTools.Where(static tool => tool.IsRunning))
        {
            childTool.IsRunning = false;
            childTool.IsError = isError;
        }
    }

    private void AddSubagentTool(AppSubagentToolStarted tool)
    {
        var subagent = Timeline.LastOrDefault(entry =>
            entry.Kind == TimelineItemKind.Subagent
            && string.Equals(entry.ActivityId, tool.SubagentCallId, StringComparison.Ordinal));
        if (subagent is null
            || subagent.ChildTools.Any(child =>
                string.Equals(child.CallId, tool.ToolCallId, StringComparison.Ordinal)))
        {
            return;
        }

        subagent.AddChildTool(new CompactToolActivityViewModel(
            tool.ToolCallId,
            tool.Name,
            _activityDescriptionFormatter.FormatTool(tool.Name, tool.Arguments)));
    }

    private void CompleteSubagentTool(AppSubagentToolCompleted tool)
    {
        var subagent = Timeline.LastOrDefault(entry =>
            entry.Kind == TimelineItemKind.Subagent
            && string.Equals(entry.ActivityId, tool.SubagentCallId, StringComparison.Ordinal));
        var childTool = subagent?.ChildTools.LastOrDefault(child =>
            string.Equals(child.CallId, tool.ToolCallId, StringComparison.Ordinal));
        if (childTool is null)
        {
            return;
        }

        childTool.IsRunning = false;
        childTool.IsError = tool.IsError;
        childTool.ToolDuration = tool.ToolDuration > TimeSpan.Zero
            ? tool.ToolDuration
            : null;
    }

    private void StopRunningChildTools(bool markAsError)
    {
        foreach (var childTool in Timeline
            .SelectMany(static entry => entry.ChildTools)
            .Where(static tool => tool.IsRunning))
        {
            childTool.IsRunning = false;
            childTool.IsError = markAsError;
        }
    }

    private async Task RefreshSuggestionsAsync(string input)
    {
        IReadOnlyList<SuggestionItemViewModel> suggestions = [];
        if (input.StartsWith('/'))
        {
            var separatorIndex = input.IndexOfAny([' ', '\t']);
            if (separatorIndex < 0)
            {
                suggestions = _controller.Commands.FindByPrefix(input)
                    .Select(command => new SuggestionItemViewModel(
                        $"{command.Name} ",
                        $"{command.Usage} — {command.Description}"))
                    .ToArray();
            }
            else
            {
                var name = input[..separatorIndex];
                var argument = input[(separatorIndex + 1)..];
                if (_controller.Commands.TryGet(name, out var command)
                    && command.GetCompletions is not null)
                {
                    var completions = await command.GetCompletions(
                        _controller,
                        argument,
                        CancellationToken.None);
                    suggestions = completions.Select(completion => new SuggestionItemViewModel(
                        $"{name} {completion.Value}",
                        completion.IsCurrent
                            ? $"{completion.DisplayText}  [현재]"
                            : completion.DisplayText)).ToArray();
                }
            }
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (!string.Equals(Draft, input, StringComparison.Ordinal))
            {
                return;
            }
            Suggestions.Clear();
            foreach (var suggestion in suggestions)
            {
                Suggestions.Add(suggestion);
            }
        });
    }

    private static string FormatUsage(ModelUsage usage) =>
        $"입력 {Format(usage.InputTokens)} · " +
        $"캐시 읽기 {Format(usage.CachedTokens)} · " +
        $"캐시 쓰기 {Format(usage.CacheWriteTokens)} · " +
        $"출력 {Format(usage.OutputTokens)} · " +
        $"추론 {Format(usage.ReasoningTokens)} · " +
        $"합계 {Format(usage.TotalTokens)}";

    private static string Format(int? value) => value?.ToString("N0") ?? "-";

    private async Task FlushDeltaAsync(StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var text = buffer.ToString();
        buffer.Clear();
        await _dispatcher.InvokeAsync(() => ApplyEvent(new AppAssistantDelta(text)));
    }
}
