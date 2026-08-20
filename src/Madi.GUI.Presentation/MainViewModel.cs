using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Madi.App;

namespace Madi.GUI.Presentation;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly MadiController _controller;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource? _operationCancellation;
    private string _draft = string.Empty;
    private string _headerTitle = "새 대화";
    private string _workingDirectory;
    private string _authenticationLabel = "확인 중";
    private string? _selectedModelName;
    private string? _currentWorkId;
    private string? _errorText;
    private bool _isBusy;
    private bool _isSignedIn;
    private bool _followLatest = true;
    private string? _submittedDraft;

    public MainViewModel(MadiController controller, IUiDispatcher dispatcher)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _workingDirectory = controller.State.WorkingDirectory;
        foreach (var model in controller.AvailableModels)
        {
            Models.Add(new ModelItemViewModel(
                model.Name,
                model.Provider,
                model.MaxContextTokens));
        }
    }

    public event EventHandler? TimelineChanged;
    public event EventHandler? ExitRequested;

    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = [];
    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; } = [];
    public ObservableCollection<SuggestionItemViewModel> Suggestions { get; } = [];
    public ObservableCollection<ModelItemViewModel> Models { get; } = [];

    public int LegacyItemCount { get; private set; }

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
        private set => SetProperty(ref _selectedModelName, value);
    }

    public string? CurrentWorkId
    {
        get => _currentWorkId;
        private set => SetProperty(ref _currentWorkId, value);
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
                OnPropertyChanged(nameof(SendButtonText));
            }
        }
    }

    public bool CanInteract => !IsBusy;
    public string SendButtonText => IsBusy ? "취소" : "전송";

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set => SetProperty(ref _isSignedIn, value);
    }

    public bool FollowLatest
    {
        get => _followLatest;
        set => SetProperty(ref _followLatest, value);
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

    public Task CreateSessionAsync(string? workId = null) =>
        ExecuteAsync(new CreateSessionAction(workId));

    public Task CreateWorkAsync(string title) =>
        ExecuteAsync(new CreateWorkAction(title));

    public Task ActivateSessionAsync(string sessionId) =>
        ExecuteAsync(new ActivateSessionAction(sessionId));

    public Task DeleteSessionAsync(string sessionId) =>
        ExecuteAsync(new DeleteSessionAction(sessionId));

    public Task ToggleAuthenticationAsync() =>
        ExecuteAsync(IsSignedIn ? new LogoutAction() : new LoginAction());

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
                ApplyConversation(conversation.Conversation);
                break;
            case AppConversationCleared:
                Timeline.Clear();
                break;
            case AppUserInput user:
                Timeline.Add(new TimelineItemViewModel(TimelineItemKind.User, user.Text));
                Draft = string.Empty;
                Suggestions.Clear();
                break;
            case AppTurnCompleted completed:
                if (completed.Succeeded)
                {
                    _submittedDraft = null;
                }
                else if (Draft.Length == 0 && _submittedDraft is not null)
                {
                    Draft = _submittedDraft;
                    _submittedDraft = null;
                }
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
            case AppToolStarted tool:
                Timeline.Add(new TimelineItemViewModel(
                    TimelineItemKind.Tool,
                    tool.Name,
                    $"### 인자\n```json\n{tool.Arguments}\n```",
                    tool.CallId,
                    isRunning: true));
                break;
            case AppToolCompleted tool:
                CompleteActivity(
                    tool.CallId,
                    tool.Name,
                    $"### 인자\n```json\n{tool.Arguments}\n```\n\n### 결과\n```text\n{tool.Result}\n```",
                    tool.IsError);
                break;
            case AppSubagentStarted subagent:
                Timeline.Add(new TimelineItemViewModel(
                    TimelineItemKind.Subagent,
                    subagent.Name,
                    $"### 프롬프트\n{subagent.Prompt}",
                    subagent.CallId,
                    isRunning: true));
                break;
            case AppSubagentCompleted subagent:
                CompleteActivity(
                    subagent.CallId,
                    subagent.Name,
                    $"### 프롬프트\n{subagent.Prompt}\n\n### 결과\n{subagent.Response}",
                    subagent.IsError);
                break;
            case AppUsageReported usage:
                Timeline.Add(new TimelineItemViewModel(
                    TimelineItemKind.Usage,
                    $"입력 {Format(usage.Usage.InputTokens)} · 출력 {Format(usage.Usage.OutputTokens)} · 합계 {Format(usage.Usage.TotalTokens)}"));
                break;
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

        FollowLatest = true;
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyState(AppState state)
    {
        IsBusy = state.IsBusy;
        SelectedModelName = state.Model.Model;
        CurrentWorkId = state.Work?.Id;
        HeaderTitle = state.Session?.Title ?? "새 대화";
        WorkingDirectory = state.WorkingDirectory;
        IsSignedIn = state.Authentication == AuthenticationState.SignedIn;
        AuthenticationLabel = state.Authentication switch
        {
            AuthenticationState.SignedIn => "로그인됨",
            AuthenticationState.SignedOut => "로그인 필요",
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
                entry.Summary,
                entry.Details,
                entry.ActivityId,
                isError: entry.IsError));
        }
        HeaderTitle = conversation.Session.Title;
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteActivity(string callId, string text, string details, bool isError)
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
