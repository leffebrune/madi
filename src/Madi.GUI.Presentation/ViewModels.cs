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
    private bool _isRunning;
    private bool _isError;

    public TimelineItemViewModel(
        TimelineItemKind kind,
        string text,
        string? details = null,
        string? activityId = null,
        bool isRunning = false,
        bool isError = false)
    {
        Kind = kind;
        _text = text;
        _details = details;
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

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public string? Details
    {
        get => _details;
        set
        {
            if (SetProperty(ref _details, value))
            {
                OnPropertyChanged(nameof(HasDetails));
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
