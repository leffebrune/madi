using System.Collections.ObjectModel;
using System.ComponentModel;
using Madi.App;
using Madi.Core;

namespace Madi.GUI.Presentation;

public enum TimelineItemKind
{
    User,
    Assistant,
    Information,
    Warning,
    Error,
    Tool,
    Subagent,
}

public sealed class CompactToolActivityViewModel : ObservableObject
{
    private bool _isRunning = true;
    private bool _isError;
    private TimeSpan? _toolDuration;

    public CompactToolActivityViewModel(string callId, string name, string description)
    {
        CallId = callId;
        Name = name;
        Description = description;
    }

    public string CallId { get; }
    public string Name { get; }
    public string Description { get; }
    public bool HasTiming => ToolDuration is not null;
    public string TimingText => ToolDuration is { } duration
        ? TimelineItemViewModel.FormatDuration(duration)
        : string.Empty;

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public bool IsError
    {
        get => _isError;
        set => SetProperty(ref _isError, value);
    }

    public TimeSpan? ToolDuration
    {
        get => _toolDuration;
        set
        {
            if (SetProperty(ref _toolDuration, value))
            {
                OnPropertyChanged(nameof(HasTiming));
                OnPropertyChanged(nameof(TimingText));
            }
        }
    }
}

public sealed class TimelineItemViewModel : ObservableObject
{
    private string _text;
    private string? _details;
    private string _description;
    private bool _isRunning;
    private bool _isError;
    private bool _isActivityExpanded;
    private bool _isStandaloneDetailsExpanded;
    private TimeSpan? _toolDuration;
    private TimeSpan? _apiDuration;
    private bool _canInteractWithConversation;
    private bool _isPersistedConversationItem;

    public TimelineItemViewModel(
        TimelineItemKind kind,
        string text,
        string? details = null,
        string? activityId = null,
        bool isRunning = false,
        bool isError = false,
        string? description = null,
        string? sessionId = null,
        int? conversationIndex = null,
        bool isPersistedConversationItem = false)
    {
        Kind = kind;
        _text = text;
        _details = details;
        _description = description ?? string.Empty;
        ActivityId = activityId;
        SessionId = sessionId;
        ConversationIndex = conversationIndex;
        _isPersistedConversationItem = isPersistedConversationItem;
        _isRunning = isRunning;
        _isError = isError;
    }

    public TimelineItemKind Kind { get; }
    public string? ActivityId { get; }
    public string? SessionId { get; }
    public int? ConversationIndex { get; }
    public ObservableCollection<CompactToolActivityViewModel> ChildTools { get; } = [];
    public bool HasChildTools => ChildTools.Count > 0;
    public string RoleLabel => Kind switch
    {
        TimelineItemKind.User => "나",
        TimelineItemKind.Assistant => "Madi",
        TimelineItemKind.Tool => "도구",
        TimelineItemKind.Subagent => "서브에이전트",
        TimelineItemKind.Warning => "주의",
        TimelineItemKind.Error => "오류",
        _ => "정보",
    };
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public bool IsUser => Kind == TimelineItemKind.User;
    public bool IsAssistant => Kind == TimelineItemKind.Assistant;
    public bool CanDeleteFromHere => IsUser
        && SessionId is not null
        && ConversationIndex is not null
        && _isPersistedConversationItem
        && _canInteractWithConversation;
    public bool IsActivity => Kind is TimelineItemKind.Tool or TimelineItemKind.Subagent;
    public bool ShowsRoleLabel => Kind is TimelineItemKind.Information
        or TimelineItemKind.Warning
        or TimelineItemKind.Error;
    public bool ShowsStandaloneDetails => HasDetails && !IsActivity;
    public string ActivityToggleLabel => IsActivityExpanded ? "접기" : "펼치기";
    public string RenderedText => IsActivity ? string.Empty : Text;
    public string ActivityDetailsMarkdown => IsActivity && IsActivityExpanded
        ? Details ?? string.Empty
        : string.Empty;
    public string StandaloneDetailsMarkdown => !IsActivity && IsStandaloneDetailsExpanded
        ? Details ?? string.Empty
        : string.Empty;
    public bool HasTiming => ApiDuration is not null || ToolDuration is not null;
    public string TimingText
    {
        get
        {
            var parts = new List<string>(2);
            if (ToolDuration is { } toolDuration)
            {
                parts.Add(FormatDuration(toolDuration));
            }
            if (ApiDuration is { } apiDuration)
            {
                parts.Add(FormatDuration(apiDuration));
            }
            return string.Join(" · ", parts);
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(RenderedText));
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string? Details
    {
        get => _details;
        set
        {
            if (SetProperty(ref _details, value))
            {
                OnPropertyChanged(nameof(HasDetails));
                OnPropertyChanged(nameof(ShowsStandaloneDetails));
                OnPropertyChanged(nameof(ActivityDetailsMarkdown));
                OnPropertyChanged(nameof(StandaloneDetailsMarkdown));
            }
        }
    }

    public bool IsActivityExpanded
    {
        get => _isActivityExpanded;
        set
        {
            if (SetProperty(ref _isActivityExpanded, value))
            {
                OnPropertyChanged(nameof(ActivityToggleLabel));
                OnPropertyChanged(nameof(ActivityDetailsMarkdown));
            }
        }
    }

    public bool IsStandaloneDetailsExpanded
    {
        get => _isStandaloneDetailsExpanded;
        set
        {
            if (SetProperty(ref _isStandaloneDetailsExpanded, value))
            {
                OnPropertyChanged(nameof(StandaloneDetailsMarkdown));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public bool IsError
    {
        get => _isError;
        set => SetProperty(ref _isError, value);
    }

    public TimeSpan? ToolDuration
    {
        get => _toolDuration;
        set
        {
            if (SetProperty(ref _toolDuration, value))
            {
                OnTimingChanged();
            }
        }
    }

    public TimeSpan? ApiDuration
    {
        get => _apiDuration;
        set
        {
            if (SetProperty(ref _apiDuration, value))
            {
                OnTimingChanged();
            }
        }
    }

    private void OnTimingChanged()
    {
        OnPropertyChanged(nameof(HasTiming));
        OnPropertyChanged(nameof(TimingText));
    }

    public void SetConversationInteraction(bool canInteract)
    {
        if (_canInteractWithConversation == canInteract)
        {
            return;
        }

        _canInteractWithConversation = canInteract;
        OnPropertyChanged(nameof(CanDeleteFromHere));
    }

    public void MarkConversationItemPersisted()
    {
        if (_isPersistedConversationItem)
        {
            return;
        }

        _isPersistedConversationItem = true;
        OnPropertyChanged(nameof(CanDeleteFromHere));
    }

    public void AddChildTool(CompactToolActivityViewModel tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        tool.PropertyChanged += OnChildToolPropertyChanged;
        ChildTools.Add(tool);
        OnPropertyChanged(nameof(HasChildTools));
    }

    private void OnChildToolPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(ChildTools));

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}분 {duration.Seconds + duration.Milliseconds / 1000d:0.#}초";
        }

        return duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds:0.##}초"
            : $"{Math.Max(0, duration.TotalMilliseconds):0}ms";
    }
}

public sealed record NavigationItemViewModel(
    string Title,
    string? SessionId,
    string? WorkId,
    int Indent,
    bool IsHeader,
    bool IsActive = false)
{
    public bool HasMenu => SessionId is not null || (IsHeader && WorkId is not null);
}

public sealed record SuggestionItemViewModel(string CompletionText, string DisplayText);

public sealed record ModelItemViewModel(
    string Name,
    string Provider,
    int MaxContextTokens,
    bool SupportsReasoning)
{
    public string Key => $"{Provider}/{Name}";
    public string DisplayName => Key;
}

public sealed record ReasoningEffortItemViewModel(
    string Key,
    string DisplayName,
    ReasoningEffort? Value);
