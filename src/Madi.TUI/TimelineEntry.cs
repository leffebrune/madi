namespace Madi.TUI;

public enum TimelineRole
{
    User,
    Assistant,
    Information,
    Warning,
    Error,
    Tool,
    Subagent,
    Usage,
}

public sealed class TimelineEntry
{
    public TimelineEntry(
        TimelineRole role,
        string summary,
        string? details = null,
        string? activityId = null,
        bool isRunning = false,
        bool isError = false)
    {
        Role = role;
        Summary = summary;
        Details = details;
        ActivityId = activityId;
        IsRunning = isRunning;
        IsError = isError;
    }

    public TimelineRole Role { get; }
    public string Summary { get; set; }
    public string? Details { get; set; }
    public string? ActivityId { get; }
    public bool IsRunning { get; set; }
    public bool IsError { get; set; }
    public bool IsExpanded { get; set; }
}
