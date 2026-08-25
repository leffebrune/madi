using System.Runtime.CompilerServices;
using Madi.Core;
using Madi.Runtime.Authentication;

namespace Madi.App;

public sealed class MadiController
{
    private readonly IAgent _agent;
    private readonly IWorkSessionStore _store;
    private readonly IOAuthAuthentication _authentication;
    private readonly IReadOnlyList<AvailableModel> _availableModels;
    private readonly string _workingDirectory;
    private ModelOptions _selectedModel;
    private Work? _currentWork;
    private Session? _currentSession;
    private AuthenticationState _authenticationState = AuthenticationState.Unknown;
    private AppRunState _runState;
    private int _isExecuting;
    private bool _initialized;

    public MadiController(
        IAgent agent,
        IWorkSessionStore store,
        ModelOptions defaultModel,
        IReadOnlyList<AvailableModel> availableModels,
        IOAuthAuthentication authentication,
        string? workingDirectory = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _selectedModel = defaultModel ?? throw new ArgumentNullException(nameof(defaultModel));
        _availableModels = availableModels ?? throw new ArgumentNullException(nameof(availableModels));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _workingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        Commands = CreateCommandCatalog();
    }

    public AppState State => CreateState();

    public IReadOnlyList<AvailableModel> AvailableModels => _availableModels;

    /// <summary>
    /// The exact system instruction currently used by the agent for every session.
    /// </summary>
    public string SystemInstructions => _agent.SystemInstructions;

    public CommandCatalog Commands { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        var current = await _store.LoadCurrentAsync(cancellationToken);
        if (current is not null
            && IsCurrentWorkspace(current.Session.WorkingDirectory))
        {
            _currentWork = current.Work;
            _currentSession = current.Session;
            _selectedModel = current.Session.Model;
        }

        try
        {
            _ = await _authentication.GetAuthorizationAsync(cancellationToken);
            _authenticationState = AuthenticationState.SignedIn;
        }
        catch (InvalidOperationException)
        {
            _authenticationState = AuthenticationState.SignedOut;
        }

        _initialized = true;
    }

    public async IAsyncEnumerable<AppEvent> ExecuteAsync(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var prompt = input.Trim();
        if (prompt.Length == 0)
        {
            yield break;
        }

        await foreach (var item in ExecuteSerializedAsync(
            token => ExecuteCoreAsync(prompt, token),
            IsAuthenticationCommand(prompt),
            cancellationToken))
        {
            yield return item;
        }
    }

    public async IAsyncEnumerable<AppEvent> ExecuteAsync(
        AppAction action,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action is SubmitPromptAction submit)
        {
            await foreach (var item in ExecuteAsync(submit.Text, cancellationToken))
            {
                yield return item;
            }
            yield break;
        }

        await foreach (var item in ExecuteSerializedAsync(
            token => ExecuteActionCoreAsync(action, token),
            action is LoginAction,
            cancellationToken))
        {
            yield return item;
        }
    }

    public async Task<NavigationSnapshot> GetNavigationAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await CreateNavigationSnapshotAsync(cancellationToken);
    }

    public ConversationSnapshot? GetCurrentConversation() =>
        _currentSession is null ? null : ConversationSnapshotFactory.Create(_currentSession);

    private async IAsyncEnumerable<AppEvent> ExecuteSerializedAsync(
        Func<CancellationToken, IAsyncEnumerable<AppEvent>> operation,
        bool isAuthentication,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
        {
            yield return new AppMessage(
                "다른 작업이 실행 중입니다. 현재 입력은 실행하지 않았습니다.",
                AppMessageKind.Warning);
            yield break;
        }

        Exception? failure = null;
        var canceled = false;
        try
        {
            _runState = isAuthentication
                ? AppRunState.Authenticating
                : AppRunState.RunningPrompt;
            yield return new AppStateChanged(CreateState());

            await using var enumerator = operation(cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                AppEvent? next = null;
                var hasNext = false;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                    if (hasNext)
                    {
                        next = enumerator.Current;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return next!;
            }

            if (canceled)
            {
                if (isAuthentication)
                {
                    yield return new AppAuthenticationProgress(
                        AuthenticationProgressKind.Canceled,
                        "로그인을 취소했습니다.");
                }
                else
                {
                    yield return new AppMessage("응답 생성을 취소했습니다.", AppMessageKind.Warning);
                }

                yield return new AppTurnCompleted(false, Canceled: true);
            }
            else if (failure is not null)
            {
                if (isAuthentication)
                {
                    yield return new AppAuthenticationProgress(
                        AuthenticationProgressKind.Failed,
                        $"로그인에 실패했습니다: {failure.Message}");
                }
                else
                {
                    yield return new AppMessage($"오류: {failure.Message}", AppMessageKind.Error);
                }

                yield return new AppTurnCompleted(false);
            }
        }
        finally
        {
            _runState = AppRunState.Idle;
            Interlocked.Exchange(ref _isExecuting, 0);
        }

        yield return new AppStateChanged(CreateState());
    }

    private async IAsyncEnumerable<AppEvent> ExecuteCoreAsync(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!input.StartsWith('/'))
        {
            await foreach (var item in RespondAsync(input, cancellationToken))
            {
                yield return item;
            }
            yield break;
        }

        var separatorIndex = input.IndexOfAny([' ', '\t']);
        var commandName = separatorIndex < 0 ? input : input[..separatorIndex];
        var argument = separatorIndex < 0 ? string.Empty : input[(separatorIndex + 1)..].Trim();
        if (!Commands.TryGet(commandName, out var command))
        {
            yield return new AppMessage(
                $"알 수 없는 명령어입니다: {commandName}",
                AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        await foreach (var item in command.Handler(this, argument, cancellationToken))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<AppEvent> ExecuteActionCoreAsync(
        AppAction action,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var source = action switch
        {
            ListModelsAction => ListModelsAsync(string.Empty, cancellationToken),
            SelectModelAction select => SelectModelAsync(select.Model, cancellationToken),
            CreateSessionAction create => CreateSessionForWorkAsync(
                create.WorkId,
                cancellationToken),
            CreateWorkAction create => CreateFreshWorkAsync(create.Title, cancellationToken),
            UpdateWorkPromptAction update => UpdateCurrentWorkPromptAsync(
                update.Prompt,
                cancellationToken),
            ActivateSessionAction activate => ActivateSessionAsync(
                activate.SessionId,
                cancellationToken),
            DeleteSessionAction delete => DeleteSessionAsync(
                delete.SessionId,
                cancellationToken),
            LoginAction => LoginAsync(string.Empty, cancellationToken),
            LogoutAction => LogoutAsync(string.Empty, cancellationToken),
            ImportLegacyWorkspaceDataAction => ImportLegacyWorkspaceDataAsync(cancellationToken),
            ExitAction => ExitAsync(string.Empty, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<AppEvent> RespondAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_availableModels.Count == 0)
        {
            yield return new AppMessage("사용 가능한 모델이 없습니다.", AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        await EnsureCurrentConversationAsync(cancellationToken);
        var session = _currentSession!;
        var conversationStart = session.Conversation.Count;
        var originalTitle = session.Title;
        var originalUpdatedAtUtc = session.UpdatedAtUtc;
        var agentCompleted = false;
        var persisted = false;
        var toolArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var subagentPrompts = new Dictionary<string, string>(StringComparer.Ordinal);

        yield return new AppUserInput(prompt);
        try
        {
            await foreach (var item in _agent.RespondAsync(
                _currentWork,
                session,
                prompt,
                cancellationToken))
            {
                switch (item)
                {
                    case AgentTextDelta delta:
                        yield return new AppAssistantDelta(delta.Text);
                        break;
                    case AgentToolCallCompleted call:
                        toolArguments[call.CallId] = call.Arguments;
                        yield return new AppToolStarted(call.CallId, call.Name, call.Arguments);
                        break;
                    case AgentToolExecutionCompleted result:
                        yield return new AppToolCompleted(
                            result.CallId,
                            result.Name,
                            toolArguments.GetValueOrDefault(result.CallId, string.Empty),
                            result.Result,
                            result.IsError);
                        break;
                    case AgentSubagentStarted started:
                        subagentPrompts[started.CallId] = started.Prompt;
                        yield return new AppSubagentStarted(
                            started.CallId,
                            started.Name,
                            started.Prompt);
                        break;
                    case AgentSubagentCompleted result:
                        yield return new AppSubagentCompleted(
                            result.CallId,
                            result.Name,
                            subagentPrompts.GetValueOrDefault(result.CallId, string.Empty),
                            result.Response,
                            result.IsError);
                        break;
                    case AgentUsageReported usage:
                        yield return new AppUsageReported(usage.Usage);
                        break;
                    case AgentResponseCompleted:
                        agentCompleted = true;
                        break;
                }
            }

            if (!agentCompleted)
            {
                throw new InvalidOperationException("에이전트가 완료 이벤트 없이 응답을 종료했습니다.");
            }

            if (conversationStart == 0
                && (string.IsNullOrWhiteSpace(session.Title) || session.Title == "새 대화"))
            {
                session.Title = CreateSessionTitle(prompt);
            }

            session.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await _store.AppendConversationAsync(session, conversationStart, cancellationToken);
            await _store.SaveSessionAsync(session, cancellationToken);
            persisted = true;
            yield return new AppTurnCompleted(true);
            yield return new AppStateChanged(CreateState());
            yield return new AppNavigationChanged(
                await CreateNavigationSnapshotAsync(cancellationToken));
        }
        finally
        {
            if (!persisted)
            {
                session.Title = originalTitle;
                session.UpdatedAtUtc = originalUpdatedAtUtc;
                if (session.Conversation.Count > conversationStart)
                {
                    session.Conversation.RemoveRange(
                        conversationStart,
                        session.Conversation.Count - conversationStart);
                }
            }
        }
    }

    private CommandCatalog CreateCommandCatalog() => new(
    [
        new AppCommand("/model-list", "/model-list", "사용 가능한 모델을 표시합니다.",
            static (controller, argument, cancellationToken) =>
                controller.ListModelsAsync(argument, cancellationToken)),
        new AppCommand("/model", "/model <모델>", "현재 모델을 변경합니다.",
            static (controller, argument, cancellationToken) =>
                controller.SelectModelAsync(argument, cancellationToken),
            static (controller, argument, cancellationToken) =>
                controller.CompleteModelsAsync(argument, cancellationToken)),
        new AppCommand("/work", "/work <제목>", "새 Work를 만들거나 현재 세션에 연결합니다.",
            static (controller, argument, cancellationToken) =>
                controller.CreateWorkAsync(argument, cancellationToken)),
        new AppCommand("/new", "/new", "새 Session을 만듭니다.",
            static (controller, argument, cancellationToken) =>
                controller.CreateSessionAsync(argument, cancellationToken)),
        new AppCommand("/login", "/login", "브라우저 OAuth 로그인을 시작합니다.",
            static (controller, argument, cancellationToken) =>
                controller.LoginAsync(argument, cancellationToken)),
        new AppCommand("/logout", "/logout", "저장된 자격 증명을 즉시 삭제합니다.",
            static (controller, argument, cancellationToken) =>
                controller.LogoutAsync(argument, cancellationToken)),
        new AppCommand("/exit", "/exit", "Madi를 종료합니다.",
            static (controller, argument, cancellationToken) =>
                controller.ExitAsync(argument, cancellationToken)),
    ]);

    private async IAsyncEnumerable<AppEvent> ListModelsAsync(
        string argument,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (argument.Length > 0)
        {
            yield return Usage("/model-list");
            yield return new AppTurnCompleted(false);
            yield break;
        }

        foreach (var model in _availableModels)
        {
            var selected = string.Equals(
                model.Name,
                _selectedModel.Model,
                StringComparison.OrdinalIgnoreCase) ? " *" : string.Empty;
            yield return new AppMessage(
                $"{model.Name} ({model.Provider}, context {model.MaxContextTokens:N0}){selected}");
        }
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> SelectModelAsync(
        string argument,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (argument.Length == 0)
        {
            yield return Usage("/model <모델>");
            yield return new AppTurnCompleted(false);
            yield break;
        }

        var model = _availableModels.FirstOrDefault(item =>
            string.Equals(item.Name, argument, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            yield return new AppMessage($"사용할 수 없는 모델입니다: {argument}", AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        _selectedModel = new ModelOptions(model.Name, _selectedModel.ReasoningEffort);
        if (_currentSession is not null)
        {
            _currentSession.Model = _selectedModel;
            _currentSession.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _store.SaveSessionAsync(_currentSession, cancellationToken);
        }

        yield return new AppMessage($"모델을 선택했습니다: {model.Name}");
        yield return new AppStateChanged(CreateState());
        yield return new AppNavigationChanged(
            await CreateNavigationSnapshotAsync(cancellationToken));
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> CreateWorkAsync(
        string argument,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (argument.Length == 0)
        {
            yield return Usage("/work <제목>");
            yield return new AppTurnCompleted(false);
            yield break;
        }

        var work = new Work
        {
            Id = Guid.CreateVersion7().ToString(),
            Title = argument,
            Context = string.Empty,
            WorkingDirectory = _workingDirectory,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _currentSession ??= CreateEmptySession(work.Id);
        _currentSession.WorkId = work.Id;
        _currentSession.WorkingDirectory = _workingDirectory;
        _currentSession.UpdatedAtUtc = DateTimeOffset.UtcNow;
        work.Sessions.Add(_currentSession);
        _currentWork = work;

        await _store.SaveWorkAsync(work, cancellationToken);
        await _store.SaveSessionAsync(_currentSession, cancellationToken);
        await _store.SetCurrentAsync(_currentSession, cancellationToken);
        yield return new AppMessage($"Work를 만들었습니다: {work.Title}");
        yield return new AppConversationLoaded(ConversationSnapshotFactory.Create(_currentSession));
        yield return new AppNavigationChanged(
            await CreateNavigationSnapshotAsync(cancellationToken));
        yield return new AppStateChanged(CreateState());
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> CreateSessionAsync(
        string argument,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (argument.Length > 0)
        {
            yield return Usage("/new");
            yield return new AppTurnCompleted(false);
            yield break;
        }

        await foreach (var item in CreateSessionForWorkAsync(
            _currentWork?.Id,
            cancellationToken))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<AppEvent> CreateSessionForWorkAsync(
        string? workId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Work? work = null;
        if (workId is not null)
        {
            work = string.Equals(_currentWork?.Id, workId, StringComparison.Ordinal)
                ? _currentWork
                : (await _store.LoadWorksAsync(cancellationToken))
                    .FirstOrDefault(item => string.Equals(item.Id, workId, StringComparison.Ordinal));
            if (work is null || !IsCurrentWorkspace(work.WorkingDirectory))
            {
                yield return new AppMessage(
                    $"현재 작업공간에서 Work를 찾을 수 없습니다: {workId}",
                    AppMessageKind.Error);
                yield return new AppTurnCompleted(false);
                yield break;
            }
        }

        var session = CreateEmptySession(workId);
        work?.Sessions.Add(session);
        await _store.SaveSessionAsync(session, cancellationToken);
        await _store.SetCurrentAsync(session, cancellationToken);
        _currentWork = work;
        _currentSession = session;
        yield return new AppMessage("새 Session을 만들었습니다.");
        yield return new AppConversationLoaded(ConversationSnapshotFactory.Create(session));
        yield return new AppNavigationChanged(
            await CreateNavigationSnapshotAsync(cancellationToken));
        yield return new AppStateChanged(CreateState());
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> CreateFreshWorkAsync(
        string title,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            yield return new AppMessage("Work 제목을 입력하세요.", AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        var now = DateTimeOffset.UtcNow;
        var work = new Work
        {
            Id = Guid.CreateVersion7().ToString(),
            Title = title.Trim(),
            Context = string.Empty,
            WorkingDirectory = _workingDirectory,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = CreateEmptySession(work.Id);
        work.Sessions.Add(session);
        await _store.SaveWorkAsync(work, cancellationToken);
        await _store.SaveSessionAsync(session, cancellationToken);
        await _store.SetCurrentAsync(session, cancellationToken);
        _currentWork = work;
        _currentSession = session;

        yield return new AppMessage($"Work를 만들었습니다: {work.Title}");
        yield return new AppConversationLoaded(ConversationSnapshotFactory.Create(session));
        yield return new AppNavigationChanged(
            await CreateNavigationSnapshotAsync(cancellationToken));
        yield return new AppStateChanged(CreateState());
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> UpdateCurrentWorkPromptAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_currentWork is null || !IsCurrentWorkspace(_currentWork.WorkingDirectory))
        {
            yield return new AppMessage(
                "프롬프트를 수정할 현재 Work가 없습니다.",
                AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        _currentWork.Context = prompt.Trim();
        _currentWork.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _store.SaveWorkAsync(_currentWork, cancellationToken);

        yield return new AppMessage("Work 프롬프트를 저장했습니다.");
        yield return new AppNavigationChanged(
            await CreateNavigationSnapshotAsync(cancellationToken));
        yield return new AppStateChanged(CreateState());
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> ActivateSessionAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            yield return new AppMessage("Session ID가 필요합니다.", AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        var conversation = await _store.LoadConversationAsync(sessionId, cancellationToken);
        if (conversation is null || !IsCurrentWorkspace(conversation.Session.WorkingDirectory))
        {
            yield return new AppMessage(
                $"현재 작업공간에서 Session을 찾을 수 없습니다: {sessionId}",
                AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        _currentWork = conversation.Work;
        _currentSession = conversation.Session;
        _selectedModel = conversation.Session.Model;
        await _store.SetCurrentAsync(conversation.Session, cancellationToken);

        yield return new AppConversationLoaded(
            ConversationSnapshotFactory.Create(conversation.Session));
        yield return new AppStateChanged(CreateState());
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> DeleteSessionAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            yield return new AppMessage("Session ID가 필요합니다.", AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        var conversation = await _store.LoadConversationAsync(sessionId, cancellationToken);
        if (conversation is null || !IsCurrentWorkspace(conversation.Session.WorkingDirectory))
        {
            yield return new AppMessage(
                $"현재 작업공간에서 Session을 찾을 수 없습니다: {sessionId}",
                AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        var deletesCurrent = string.Equals(
            _currentSession?.Id,
            sessionId,
            StringComparison.Ordinal);
        CurrentConversation? replacement = null;
        if (deletesCurrent)
        {
            var candidates = (await _store.LoadSessionsAsync(cancellationToken))
                .Where(session => !string.Equals(session.Id, sessionId, StringComparison.Ordinal)
                                  && IsCurrentWorkspace(session.WorkingDirectory))
                .OrderByDescending(static session => session.UpdatedAtUtc);
            foreach (var candidate in candidates)
            {
                replacement = await _store.LoadConversationAsync(
                    candidate.Id,
                    cancellationToken);
                if (replacement is not null)
                {
                    break;
                }
            }

            // Move the durable pointer first so it can never reference a deleted session.
            if (replacement is not null)
            {
                await _store.SetCurrentAsync(replacement.Session, cancellationToken);
            }
        }

        bool deleted;
        try
        {
            deleted = await _store.DeleteSessionAsync(sessionId, cancellationToken);
        }
        catch
        {
            if (deletesCurrent && replacement is not null)
            {
                await _store.SetCurrentAsync(conversation.Session, CancellationToken.None);
            }
            throw;
        }

        if (!deleted)
        {
            if (deletesCurrent && replacement is not null)
            {
                await _store.SetCurrentAsync(conversation.Session, CancellationToken.None);
            }
            yield return new AppMessage(
                $"Session을 삭제하지 못했습니다: {sessionId}",
                AppMessageKind.Error);
            yield return new AppTurnCompleted(false);
            yield break;
        }

        if (deletesCurrent)
        {
            _currentWork = replacement?.Work;
            _currentSession = replacement?.Session;
            if (_currentSession is null)
            {
                yield return new AppConversationCleared();
            }
            else
            {
                _selectedModel = _currentSession.Model;
                yield return new AppConversationLoaded(
                    ConversationSnapshotFactory.Create(_currentSession));
            }
        }

        yield return new AppNavigationChanged(
            await CreateNavigationSnapshotAsync(cancellationToken));
        yield return new AppStateChanged(CreateState());
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> ImportLegacyWorkspaceDataAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var works = await _store.LoadWorksAsync(cancellationToken);
        var sessions = await _store.LoadSessionsAsync(cancellationToken);
        var imported = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var work in works.Where(static item => item.WorkingDirectory is null))
        {
            work.WorkingDirectory = _workingDirectory;
            work.UpdatedAtUtc = work.UpdatedAtUtc == default ? now : work.UpdatedAtUtc;
            work.CreatedAtUtc = work.CreatedAtUtc == default ? work.UpdatedAtUtc : work.CreatedAtUtc;
            await _store.SaveWorkAsync(work, cancellationToken);
            imported++;
        }

        foreach (var session in sessions.Where(static item => item.WorkingDirectory is null))
        {
            session.WorkingDirectory = _workingDirectory;
            session.UpdatedAtUtc = session.UpdatedAtUtc == default ? now : session.UpdatedAtUtc;
            session.CreatedAtUtc = session.CreatedAtUtc == default
                ? session.UpdatedAtUtc
                : session.CreatedAtUtc;
            await _store.SaveSessionAsync(session, cancellationToken);
            imported++;
        }

        yield return new AppMessage($"기존 항목 {imported:N0}개를 현재 작업공간에 연결했습니다.");
        yield return new AppNavigationChanged(
            await CreateNavigationSnapshotAsync(cancellationToken));
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> LoginAsync(
        string argument,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (argument.Length > 0)
        {
            yield return Usage("/login");
            yield return new AppTurnCompleted(false);
            yield break;
        }

        yield return new AppAuthenticationProgress(
            AuthenticationProgressKind.Started,
            "브라우저에서 로그인을 완료하세요.");
        await _authentication.LoginAsync(cancellationToken);
        _authenticationState = AuthenticationState.SignedIn;
        yield return new AppAuthenticationProgress(
            AuthenticationProgressKind.Succeeded,
            "로그인 정보를 저장했습니다.");
        yield return new AppStateChanged(CreateState());
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> LogoutAsync(
        string argument,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (argument.Length > 0)
        {
            yield return Usage("/logout");
            yield return new AppTurnCompleted(false);
            yield break;
        }

        await _authentication.LogoutAsync(cancellationToken);
        _authenticationState = AuthenticationState.SignedOut;
        yield return new AppAuthenticationProgress(
            AuthenticationProgressKind.SignedOut,
            "로그인 정보를 삭제했습니다.");
        yield return new AppStateChanged(CreateState());
        yield return new AppTurnCompleted(true);
    }

    private async IAsyncEnumerable<AppEvent> ExitAsync(
        string argument,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (argument.Length > 0)
        {
            yield return Usage("/exit");
            yield return new AppTurnCompleted(false);
            yield break;
        }

        yield return new AppExitRequested();
        yield return new AppTurnCompleted(true);
    }

    private ValueTask<IReadOnlyList<CommandCompletion>> CompleteModelsAsync(
        string argument,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CommandCompletion> result = _availableModels
            .Where(model => model.Name.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
            .Select(model => new CommandCompletion(
                model.Name,
                $"{model.Name} ({model.Provider})",
                string.Equals(model.Name, _selectedModel.Model, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return ValueTask.FromResult(result);
    }

    private async Task EnsureCurrentConversationAsync(CancellationToken cancellationToken)
    {
        if (_currentSession is not null)
        {
            return;
        }

        _currentWork = null;
        _currentSession = CreateEmptySession(workId: null);
        await _store.SaveSessionAsync(_currentSession, cancellationToken);
        await _store.SetCurrentAsync(_currentSession, cancellationToken);
    }

    private Session CreateEmptySession(string? workId) => new()
    {
        Id = Guid.CreateVersion7().ToString(),
        Title = "새 대화",
        WorkId = workId,
        WorkingDirectory = _workingDirectory,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        Model = _selectedModel,
    };

    private async Task<NavigationSnapshot> CreateNavigationSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var works = await _store.LoadWorksAsync(cancellationToken);
        var sessions = await _store.LoadSessionsAsync(cancellationToken);
        var sessionsByWorkId = sessions
            .Where(static session => session.WorkId is not null)
            .ToLookup(static session => session.WorkId!, StringComparer.Ordinal);
        var currentWorks = works
            .Where(work => IsCurrentWorkspace(work.WorkingDirectory))
            .Select(work =>
            {
                var children = sessionsByWorkId[work.Id]
                    .Where(session => IsCurrentWorkspace(session.WorkingDirectory))
                    .OrderByDescending(static session => session.UpdatedAtUtc)
                    .Select(CreateSessionNavigationSnapshot)
                    .ToArray();
                var updatedAt = children.Length == 0
                    ? work.UpdatedAtUtc
                    : children.Max(static session => session.UpdatedAtUtc);
                return new WorkNavigationSnapshot(
                    work.Id,
                    work.Title,
                    work.CreatedAtUtc,
                    updatedAt,
                    children);
            })
            .OrderByDescending(static work => work.UpdatedAtUtc)
            .ToArray();
        var standalone = sessions
            .Where(session => session.WorkId is null
                              && IsCurrentWorkspace(session.WorkingDirectory))
            .OrderByDescending(static session => session.UpdatedAtUtc)
            .Select(CreateSessionNavigationSnapshot)
            .ToArray();
        var legacyWorkIds = works
            .Where(static work => work.WorkingDirectory is null)
            .Select(static work => work.Id)
            .ToHashSet(StringComparer.Ordinal);
        var legacyCount = legacyWorkIds.Count
                          + sessions.Count(session => session.WorkingDirectory is null);

        return new NavigationSnapshot(
            _workingDirectory,
            currentWorks,
            standalone,
            legacyCount);
    }

    private static SessionNavigationSnapshot CreateSessionNavigationSnapshot(Session session) =>
        new(
            session.Id,
            session.Title,
            session.WorkId,
            session.CreatedAtUtc,
            session.UpdatedAtUtc);

    private bool IsCurrentWorkspace(string? path) =>
        path is not null
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(_workingDirectory),
            StringComparison.OrdinalIgnoreCase);

    private AppState CreateState() => new(
        _selectedModel,
        _currentWork is null
            ? null
            : new WorkSnapshot(_currentWork.Id, _currentWork.Title, _currentWork.Context),
        _currentSession is null
            ? null
            : new SessionSnapshot(_currentSession.Id, _currentSession.Title, _currentSession.WorkId),
        _runState,
        _authenticationState,
        _workingDirectory);

    private static AppMessage Usage(string usage) =>
        new($"사용법: {usage}", AppMessageKind.Error);

    private static bool IsAuthenticationCommand(string input) =>
        input.Equals("/login", StringComparison.OrdinalIgnoreCase)
        || input.StartsWith("/login ", StringComparison.OrdinalIgnoreCase);

    private static string CreateSessionTitle(string prompt)
    {
        var normalized = string.Join(
            ' ',
            prompt.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        const int maximumLength = 40;
        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..maximumLength].TrimEnd()}…";
    }
}
