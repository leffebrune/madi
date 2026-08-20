using Madi.Core;

namespace Madi.App;

public abstract record AppEvent;

public enum AppMessageKind
{
    Information,
    Warning,
    Error,
}

public sealed record AppMessage(string Text, AppMessageKind Kind = AppMessageKind.Information)
    : AppEvent;

public sealed record AppUserInput(string Text) : AppEvent;

public sealed record AppAssistantDelta(string Text) : AppEvent;

public sealed record AppToolStarted(
    string CallId,
    string Name,
    string Arguments) : AppEvent;

public sealed record AppToolCompleted(
    string CallId,
    string Name,
    string Arguments,
    string Result,
    bool IsError) : AppEvent;

public sealed record AppSubagentStarted(
    string CallId,
    string Name,
    string Prompt) : AppEvent;

public sealed record AppSubagentCompleted(
    string CallId,
    string Name,
    string Prompt,
    string Response,
    bool IsError) : AppEvent;

public sealed record AppUsageReported(ModelUsage Usage) : AppEvent;

public enum AuthenticationProgressKind
{
    Started,
    Succeeded,
    Failed,
    Canceled,
    SignedOut,
}

public sealed record AppAuthenticationProgress(
    AuthenticationProgressKind Kind,
    string Message) : AppEvent;

public sealed record AppStateChanged(AppState State) : AppEvent;

public sealed record AppTurnCompleted(bool Succeeded, bool Canceled = false) : AppEvent;

public sealed record AppExitRequested : AppEvent;
