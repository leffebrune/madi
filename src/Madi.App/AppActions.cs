namespace Madi.App;

public abstract record AppAction;

public sealed record SubmitPromptAction(string Text) : AppAction;

public sealed record ListModelsAction : AppAction;

public sealed record SelectModelAction(string Model) : AppAction;

public sealed record CreateSessionAction(string? WorkId = null) : AppAction;

public sealed record CreateWorkAction(string Title) : AppAction;

public sealed record ActivateSessionAction(string SessionId) : AppAction;

public sealed record DeleteSessionAction(string SessionId) : AppAction;

public sealed record LoginAction : AppAction;

public sealed record LogoutAction : AppAction;

public sealed record ImportLegacyWorkspaceDataAction : AppAction;

public sealed record ExitAction : AppAction;
