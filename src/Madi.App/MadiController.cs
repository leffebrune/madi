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

    public CommandCatalog Commands { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        var current = await _store.LoadCurrentAsync(cancellationToken);
        if (current is not null)
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
        await InitializeAsync(cancellationToken);

        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
        {
            yield return new AppMessage(
                "다른 작업이 실행 중입니다. 현재 입력은 실행하지 않았습니다.",
                AppMessageKind.Warning);
            yield break;
        }

        var prompt = input.Trim();
        if (prompt.Length == 0)
        {
            Interlocked.Exchange(ref _isExecuting, 0);
            yield break;
        }

        Exception? failure = null;
        var canceled = false;
        try
        {
            _runState = IsAuthenticationCommand(prompt)
                ? AppRunState.Authenticating
                : AppRunState.RunningPrompt;
            yield return new AppStateChanged(CreateState());

            await using var enumerator = ExecuteCoreAsync(prompt, cancellationToken)
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
                if (IsAuthenticationCommand(prompt))
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
                if (IsAuthenticationCommand(prompt))
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

            await _store.AppendConversationAsync(session, conversationStart, cancellationToken);
            await _store.SaveSessionAsync(session, cancellationToken);
            persisted = true;
            yield return new AppTurnCompleted(true);
            yield return new AppStateChanged(CreateState());
        }
        finally
        {
            if (!persisted)
            {
                session.Title = originalTitle;
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
            await _store.SaveSessionAsync(_currentSession, cancellationToken);
        }

        yield return new AppMessage($"모델을 선택했습니다: {model.Name}");
        yield return new AppStateChanged(CreateState());
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
        };
        _currentSession ??= CreateEmptySession(work.Id);
        _currentSession.WorkId = work.Id;
        work.Sessions.Add(_currentSession);
        _currentWork = work;

        await _store.SaveWorkAsync(work, cancellationToken);
        await _store.SaveSessionAsync(_currentSession, cancellationToken);
        await _store.SetCurrentAsync(_currentSession, cancellationToken);
        yield return new AppMessage($"Work를 만들었습니다: {work.Title}");
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

        var session = CreateEmptySession(_currentWork?.Id);
        _currentWork?.Sessions.Add(session);
        await _store.SaveSessionAsync(session, cancellationToken);
        await _store.SetCurrentAsync(session, cancellationToken);
        _currentSession = session;
        yield return new AppMessage("새 Session을 만들었습니다.");
        yield return new AppStateChanged(CreateState());
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
        Model = _selectedModel,
    };

    private AppState CreateState() => new(
        _selectedModel,
        _currentWork is null ? null : new WorkSnapshot(_currentWork.Id, _currentWork.Title),
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
