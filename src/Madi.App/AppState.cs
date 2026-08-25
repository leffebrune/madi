using Madi.Core;

namespace Madi.App;

public enum AppRunState
{
    Idle,
    RunningPrompt,
    Authenticating,
}

public enum AuthenticationState
{
    Unknown,
    SignedOut,
    SignedIn,
}

public sealed record WorkSnapshot(string Id, string Title, string Prompt);

public sealed record SessionSnapshot(string Id, string Title, string? WorkId);

public sealed record AppState(
    ModelOptions Model,
    WorkSnapshot? Work,
    SessionSnapshot? Session,
    AppRunState RunState,
    AuthenticationState Authentication,
    string WorkingDirectory)
{
    public bool IsBusy => RunState is not AppRunState.Idle;
}
