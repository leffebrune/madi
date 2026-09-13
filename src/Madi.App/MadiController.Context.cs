using Madi.Core;

namespace Madi.App;

public sealed partial class MadiController
{
    private bool _writesCurrent = true;
    internal IWorkSessionStore Store => _store;
    internal Session? Session => _currentSession;

    // Lightweight command context: all providers, tools, authentication and storage stay shared.
    internal async Task<MadiController> CreateContextAsync(CurrentConversation? conversation,
        ModelOptions? model = null, CancellationToken cancellationToken = default)
    {
        var context = new MadiController(_agent, _store, model ?? _selectedModel,
            _availableModels, _authentication, _workingDirectory)
        {
            _writesCurrent = false,
            _initialized = true,
        };
        if (conversation is not null)
        {
            if (!IsCurrentWorkspace(conversation.Session.WorkingDirectory))
                throw new InvalidOperationException("현재 프로젝트의 세션이 아닙니다.");
            context._selectedModel = context.ResolvePersistedModel(conversation.Session, out var message);
            context.InitializationMessage = message;
            context._currentWork = conversation.Work;
            context._currentSession = conversation.Session;
            context._currentSession.Model = context._selectedModel;
        }
        await context.RefreshAuthenticationAsync(cancellationToken);
        return context;
    }

    internal void RefreshWork(Work work)
    {
        if (_currentWork?.Id == work.Id) _currentWork = work;
    }

    internal void CopySharedAuthentication(MadiController source)
    {
        if (_authenticationKind == AuthenticationKind.OAuth
            && source._authenticationKind == AuthenticationKind.OAuth)
            _authenticationState = source._authenticationState;
    }

    private Task WriteCurrentAsync(Session session, CancellationToken cancellationToken) =>
        _writesCurrent ? _store.SetCurrentAsync(session, cancellationToken) : Task.CompletedTask;
}
