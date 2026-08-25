using System.Collections.ObjectModel;
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

public sealed class TimelineItemViewModel : ObservableObject
{
    private string _text;
    private string? _details;
    private string _description;
    private bool _isRunning;
    private bool _isError;
    private bool _isActivityExpanded;

    public TimelineItemViewModel(
        TimelineItemKind kind,
        string text,
        string? details = null,
        string? activityId = null,
        bool isRunning = false,
        bool isError = false,
        string? description = null)
    {
        Kind = kind;
        _text = text;
        _details = details;
        _description = description ?? string.Empty;
        ActivityId = activityId;
        _isRunning = isRunning;
        _isError = isError;
    }

    public TimelineItemKind Kind { get; }
    public string? ActivityId { get; }
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
    public bool IsAssistant => Kind == TimelineItemKind.Assistant;
    public bool IsActivity => Kind is TimelineItemKind.Tool or TimelineItemKind.Subagent;
    public bool ShowsRoleLabel => Kind is TimelineItemKind.Information
        or TimelineItemKind.Warning
        or TimelineItemKind.Error;
    public bool ShowsStandaloneDetails => HasDetails && !IsActivity;
    public string ActivityToggleLabel => IsActivityExpanded ? "접기" : "펼치기";

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
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
}

public sealed record NavigationItemViewModel(
    string Title,
    string? SessionId,
    string? WorkId,
    int Indent,
    bool IsHeader,
    bool IsActive = false);

public sealed record SuggestionItemViewModel(string CompletionText, string DisplayText);

public sealed record ModelItemViewModel(
    string Name,
    string Provider,
    int MaxContextTokens);
