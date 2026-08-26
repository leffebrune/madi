using Madi.Core;

namespace Madi.App;

public enum AppRunState
{
    Idle,
    RunningPrompt,
    Authenticating,
}

public enum AuthenticationKind
{
    None,
    OAuth,
    ApiKey,
}

public enum AuthenticationState
{
    Unknown,
    NotRequired,
    ActionRequired,
    Ready,
}

public sealed record WorkSnapshot(string Id, string Title, string Prompt);

public sealed record SessionSnapshot(string Id, string Title, string? WorkId);

public sealed record AppState(
    ModelOptions Model,
    WorkSnapshot? Work,
    SessionSnapshot? Session,
    AppRunState RunState,
    AuthenticationKind AuthenticationKind,
    AuthenticationState Authentication,
    string WorkingDirectory)
{
    public bool IsBusy => RunState is not AppRunState.Idle;
}
