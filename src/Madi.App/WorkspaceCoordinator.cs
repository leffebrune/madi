using Madi.Core;

namespace Madi.App;

public sealed class WorkspaceTab(TabId id, int number)
{
    public TabId Id { get; } = id;
    public int Number { get; } = number;
    public string Name => $"탭 {Number}";
    public SessionRuntime? Selected { get; internal set; }
    public bool IsClosing { get; internal set; }
    internal CancellationTokenSource Lifetime { get; } = new();
    internal HashSet<Task> Navigations { get; } = [];
}

public sealed class WorkConflictException(WorkSnapshot latest)
    : InvalidOperationException("다른 탭에서 Work를 변경했습니다. 초안을 보존했습니다. 최신 저장본을 확인하세요.")
{
    public WorkSnapshot Latest { get; } = latest;
}

/// <summary>One project, shared services and data, independent session executions.</summary>
public sealed class WorkspaceCoordinator
{
    private readonly MadiController _prototype;
    private readonly IWorkSessionStore _store;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly SemaphoreSlim _pointer = new(1, 1);
    private readonly Dictionary<string, (TabId Owner, SessionRuntime Runtime)> _runtimes = [];
    private readonly List<WorkspaceTab> _tabs = [];
    private int _nextNumber;
    private volatile bool _authenticationBusy;
    private bool _closing;
    private TabId? _active;
    public WorkspaceCoordinator(MadiController prototype)
    {
        _prototype = prototype;
        _store = prototype.Store;
    }

    public SessionOwnershipRegistry Ownership { get; } = new();
    public IReadOnlyList<WorkspaceTab> Tabs { get { lock (_gate) return _tabs.ToArray(); } }
    public TabId? ActiveTab { get { lock (_gate) return _active; } }
    public event EventHandler? Changed;
    public event EventHandler<WorkSnapshot>? WorkChanged;
    public string WorkingDirectory => _prototype.State.WorkingDirectory;
    public int RunningCount(TabId tab)
    {
        lock (_gate) return _runtimes.Values.Count(r => r.Owner == tab && r.Runtime.IsRunning);
    }
    public IReadOnlyList<string> RunningTitles(TabId tab)
    {
        lock (_gate) return _runtimes.Values.Where(r => r.Owner == tab && r.Runtime.IsRunning)
            .Select(r => r.Runtime.Controller.State.Session!.Title).ToArray();
    }
    public bool HasUnseenResult(TabId tab)
    {
        lock (_gate) return _runtimes.Values.Any(r => r.Owner == tab && r.Runtime.HasUnseenResult);
    }
    public SessionRuntime? FindRuntime(string session)
    {
        lock (_gate) return _runtimes.GetValueOrDefault(session).Runtime;
    }
    public WorkspaceTab GetTab(TabId tab)
    {
        lock (_gate) return _tabs.Single(t => t.Id == tab);
    }
    public Task<NavigationSnapshot> GetNavigationAsync(CancellationToken token = default) =>
        _prototype.GetNavigationAsync(token);

    public async Task<WorkspaceTab> InitializeAsync(CancellationToken token = default)
    {
        await _prototype.InitializeAsync(token);
        var tab = Register();
        try
        {
            if (_prototype.State.Session is { } session)
                await SelectAsync(tab.Id, session.Id, token);
            else
                await CreateSessionAsync(tab.Id, null, _prototype.State.Model, token);
            await ActivateTabAsync(tab.Id, token);
            return tab;
        }
        catch { await CloseTabAsync(tab.Id); throw; }
    }

    public async Task<WorkspaceTab> NewTabAsync(TabId source, string? sessionId = null,
        CancellationToken token = default)
    {
        var from = GetTab(source);
        var model = from.Selected!.Controller.State.Model;
        var work = from.Selected.Controller.State.Work?.Id;
        // Reserve before publishing a tab. A rejected open never leaves an empty tab.
        if (sessionId is not null && Ownership.Owner(sessionId) is { } owner)
            throw new SessionInUseException(sessionId, owner);
        var tab = Register();
        try
        {
            if (sessionId is not null) await SelectAsync(tab.Id, sessionId, token);
            else await CreateSessionAsync(tab.Id, work, model, token);
            await ActivateTabAsync(tab.Id, token);
            return tab;
        }
        catch { await CloseTabAsync(tab.Id); throw; }
    }

    private WorkspaceTab Register()
    {
        lock (_gate)
        {
            if (_closing) throw new InvalidOperationException("프로젝트를 닫는 중입니다.");
            var tab = new WorkspaceTab(TabId.New(), ++_nextNumber);
            Ownership.Register(tab.Id);
            _tabs.Add(tab);
            return tab;
        }
    }

    public Task<bool> SelectAsync(TabId tabId, string sessionId, CancellationToken token = default)
    {
        lock (_gate)
        {
            var tab = GetTab(tabId);
            if (tab.IsClosing) throw new InvalidOperationException("닫는 중인 탭입니다.");
            var task = SelectCoreAsync(tab, sessionId, token);
            tab.Navigations.Add(task);
            _ = task.ContinueWith(_ => { lock (_gate) tab.Navigations.Remove(task); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<bool> SelectCoreAsync(WorkspaceTab tab, string sessionId, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, tab.Lifetime.Token);
        using var reservation = Ownership.Reserve(tab.Id, sessionId);
        SessionRuntime? runtime;
        lock (_gate)
        {
            var existing = _runtimes.GetValueOrDefault(sessionId);
            runtime = existing.Owner == tab.Id ? existing.Runtime : null;
        }
        if (runtime is null)
        {
            var conversation = await _store.LoadConversationAsync(sessionId, cancellation.Token)
                ?? throw new InvalidOperationException("세션을 찾을 수 없습니다.");
            var controller = await _prototype.CreateContextAsync(conversation, cancellationToken: cancellation.Token);
            runtime = new SessionRuntime(controller);
        }
        cancellation.Token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!Ownership.Commit(reservation)) return false;
            _runtimes[sessionId] = (tab.Id, runtime);
            tab.Selected = runtime;
            if (_active == tab.Id) runtime.HasUnseenResult = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await SaveActivePointerAsync(cancellation.Token);
        return true;
    }

    public async Task ActivateTabAsync(TabId tabId, CancellationToken token = default)
    {
        lock (_gate)
        {
            var tab = GetTab(tabId);
            if (tab.IsClosing) throw new InvalidOperationException("닫는 중인 탭입니다.");
            _active = tabId;
            if (tab.Selected is { } selected) selected.HasUnseenResult = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await SaveActivePointerAsync(token);
    }

    public async Task GoToOwnerAsync(string session, CancellationToken token = default)
    {
        var owner = Ownership.Owner(session) ?? throw new InvalidOperationException("세션 점유가 해제되었습니다. 다시 선택하세요.");
        if (await SelectAsync(owner, session, token)) await ActivateTabAsync(owner, token);
    }

    private async Task SaveActivePointerAsync(CancellationToken token)
    {
        await _pointer.WaitAsync(token);
        try
        {
            // Read only after acquiring the writer gate; late background operations cannot restore old state.
            Session? session;
            lock (_gate) session = _tabs.FirstOrDefault(t => t.Id == _active)?.Selected?.Controller.Session;
            if (session is not null) await _store.SetCurrentAsync(session, token);
        }
        finally { _pointer.Release(); }
    }

    public async Task CreateSessionAsync(TabId tabId, string? workId, ModelOptions? model = null,
        CancellationToken token = default)
    {
        await _changes.WaitAsync(token);
        string id;
        try
        {
            var tab = GetTab(tabId);
            if (tab.IsClosing) throw new InvalidOperationException("닫는 중인 탭입니다.");
            var context = await _prototype.CreateContextAsync(null,
                model ?? tab.Selected?.Controller.State.Model, token);
            await foreach (var item in context.ExecuteAsync(new CreateSessionAction(workId), token))
                if (item is AppMessage { Kind: AppMessageKind.Error } error)
                    throw new InvalidOperationException(error.Text);
            id = context.State.Session?.Id ?? throw new InvalidOperationException("세션을 만들지 못했습니다.");
        }
        finally { _changes.Release(); }
        await SelectAsync(tabId, id, token);
    }

    public Task ExecuteAsync(TabId tabId, AppAction action, CancellationToken token = default) =>
        ExecuteCoreAsync(tabId, action, true, null, token);

    /// <summary>Returns on acceptance, not completion. Admission shares the start/close gate,
    /// and a captured session never follows later navigation.</summary>
    public async Task SubmitPromptAsync(TabId tabId, string sessionId, Guid observedRunId,
        Guid messageId, string text, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length == 0) return;
        var tab = GetTab(tabId);
        var runtime = FindRuntime(sessionId) ?? throw new InvalidOperationException("세션을 찾을 수 없습니다.");
        var input = new AgentInput(messageId, text);
        while (true)
        {
            Task? finalizing = null;
            var command = text.StartsWith('/');
            await _changes.WaitAsync(token);
            try
            {
                if (Ownership.Owner(sessionId) != tabId || tab.IsClosing || _closing)
                    throw new InvalidOperationException("이 탭에서 입력을 전달할 수 없습니다.");
                if (_authenticationBusy)
                    throw new InvalidOperationException("공통 인증 작업이 진행 중입니다.");
                if (runtime.HasAccepted(messageId)) return;
                if (runtime.IsRunning)
                {
                    if (command)
                        throw new InvalidOperationException("실행 중에는 명령어 대신 추가 작업 지시만 전송할 수 있습니다.");
                    if (observedRunId != runtime.RunId)
                        throw new InvalidOperationException("실행이 변경되었습니다. 현재 상태를 확인하고 다시 전송하세요.");
                    if (runtime.TrySubmit(input, observedRunId)) return;
                    finalizing = runtime.Completion;
                }
                else if (!command)
                {
                    // Holding _changes prevents a second submit/start or tab close from overtaking admission.
                    await StartRuntimeAsync(tab, runtime, new SubmitPromptAction(text), token, messageId);
                }
            }
            finally { _changes.Release(); }
            if (finalizing is not null)
            {
                await finalizing.WaitAsync(token);
                if (!runtime.CompletedSuccessfully)
                    throw new InvalidOperationException("작업이 중단되었습니다. 내용을 확인하고 다시 전송하세요.");
                continue;
            }
            if (command) await ExecuteCoreAsync(tabId, new SubmitPromptAction(text), false, sessionId, token);
            else Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
    }

    private async Task ExecuteCoreAsync(TabId tabId, AppAction action, bool waitForCompletion,
        string? expectedSessionId, CancellationToken token)
    {
        var tab = GetTab(tabId);
        var runtime = tab.Selected ?? throw new InvalidOperationException("선택한 세션이 없습니다.");
        if (expectedSessionId is not null && runtime.SessionId != expectedSessionId)
            throw new InvalidOperationException("선택한 세션이 변경되었습니다. 초안을 확인하세요.");
        if (action is SubmitPromptAction submit && submit.Text.TrimStart().StartsWith('/'))
        {
            var parts = submit.Text.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            var argument = parts.Length > 1 ? parts[1] : "";
            action = parts[0].ToLowerInvariant() switch
            {
                "/new" when argument.Length == 0 => new CreateSessionAction(runtime.Controller.State.Work?.Id),
                "/login" when argument.Length == 0 => new LoginAction(),
                "/logout" when argument.Length == 0 => new LogoutAction(),
                _ => action,
            };
        }
        switch (action)
        {
            case ActivateSessionAction select: await SelectAsync(tabId, select.SessionId, token); return;
            case CreateSessionAction create:
                await CreateSessionAsync(tabId, create.WorkId, runtime.Controller.State.Model, token); return;
            case CreateWorkAction create: await CreateWorkAsync(tabId, create.Title, runtime.Controller.State.Model, token); return;
            case DeleteSessionAction delete: await DeleteAsync(tabId, delete.SessionId, null, token); return;
            case DeleteWorkAction delete: await DeleteAsync(tabId, null, delete.WorkId, token); return;
            case UpdateWorkPromptAction:
                throw new InvalidOperationException("Work 저장에는 편집 기준 버전이 필요합니다.");
        }

        Task operation;
        await _changes.WaitAsync(token);
        try
        {
            if (tab.IsClosing) throw new InvalidOperationException("닫는 중인 탭입니다.");
            if (_authenticationBusy) throw new InvalidOperationException("공통 인증 작업이 진행 중입니다.");
            if (action is LogoutAction && HasOAuthRuns())
                throw new InvalidOperationException("OAuth를 사용하는 실행 또는 저장 중에는 로그아웃할 수 없습니다. 작업 종료 후 다시 시도하세요.");
            if (action is DeleteConversationFromAction truncate && truncate.SessionId != runtime.SessionId)
                throw new InvalidOperationException("현재 선택한 세션에서만 대화를 삭제할 수 있습니다.");
            operation = await StartRuntimeAsync(tab, runtime, action, token);
        }
        finally { _changes.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        if (waitForCompletion) await operation;
    }

    // Caller owns _changes; this returns the execution task without awaiting it.
    private async Task<Task> StartRuntimeAsync(WorkspaceTab tab, SessionRuntime runtime,
        AppAction action, CancellationToken token, Guid? initialMessageId = null)
    {
        // Work is reloaded at acceptance. Agent takes an immutable per-turn prompt/model snapshot.
        if (runtime.Controller.State.Work is { } currentWork)
        {
            var work = (await _store.LoadWorksAsync(token)).Single(w => w.Id == currentWork.Id);
            runtime.Controller.RefreshWork(work);
        }
        if (tab.IsClosing || _closing) throw new InvalidOperationException("닫는 중인 탭입니다.");
        var runId = Guid.NewGuid();
        IDisposable lease = action is DeleteConversationFromAction
            ? Ownership.BeginMutation(tab.Id, [runtime.SessionId])
            : Ownership.BeginRun(tab.Id, runtime.SessionId, runId);
        var authentication = action is LoginAction or LogoutAction;
        if (authentication) _authenticationBusy = true;
        var operation = runtime.Start(action, runId, lease, () =>
        {
            SessionRuntime[] authenticationObservers = [];
            lock (_gate)
            {
                if (authentication)
                {
                    _prototype.CopySharedAuthentication(runtime.Controller);
                    authenticationObservers = _runtimes.Values.Select(r => r.Runtime).ToArray();
                    foreach (var observer in authenticationObservers)
                        observer.Controller.CopySharedAuthentication(runtime.Controller);
                    _authenticationBusy = false;
                }
                runtime.HasUnseenResult = _active != tab.Id || tab.Selected != runtime;
            }
            foreach (var observer in authenticationObservers)
                observer.Publish(new AppStateChanged(observer.Controller.State));
            Changed?.Invoke(this, EventArgs.Empty);
        }, initialMessageId);
        // Observe faults even when the initiating view is no longer present.
        _ = operation.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return operation;
    }

    private bool HasOAuthRuns()
    {
        lock (_gate) return _runtimes.Values.Any(r => r.Runtime.IsRunning
            && r.Runtime.Controller.State.AuthenticationKind == AuthenticationKind.OAuth);
    }

    private async Task CreateWorkAsync(TabId tabId, string title, ModelOptions model, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("Work 제목을 입력하세요.");
        var work = new Work { Id = Guid.CreateVersion7().ToString(), Title = title.Trim(),
            WorkingDirectory = WorkingDirectory, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await _changes.WaitAsync(token);
        try
        {
            if (GetTab(tabId).IsClosing) throw new InvalidOperationException("닫는 중인 탭입니다.");
            await _store.SaveWorkAsync(work, token);
        }
        finally { _changes.Release(); }
        await CreateSessionAsync(tabId, work.Id, model, token);
    }

    public async Task SaveWorkAsync(TabId tabId, string workId, string expectedPrompt,
        string prompt, CancellationToken token = default)
    {
        await _changes.WaitAsync(token);
        Work work;
        try
        {
            if (GetTab(tabId).IsClosing) throw new InvalidOperationException("닫는 중인 탭입니다.");
            work = (await _store.LoadWorksAsync(token)).Single(w => w.Id == workId);
            if (work.Context != expectedPrompt)
                throw new WorkConflictException(new WorkSnapshot(work.Id, work.Title, work.Context));
            work.Context = prompt.Trim(); work.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _store.SaveWorkAsync(work, token);
            lock (_gate)
                foreach (var entry in _runtimes.Values)
                    if (!entry.Runtime.IsRunning) entry.Runtime.Controller.RefreshWork(work);
        }
        finally { _changes.Release(); }
        WorkChanged?.Invoke(this, new WorkSnapshot(work.Id, work.Title, work.Context));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task DeleteAsync(TabId tabId, string? sessionId, string? workId, CancellationToken token)
    {
        var tab = GetTab(tabId);
        var replace = false;
        await _changes.WaitAsync(token);
        try
        {
            var sessions = await _store.LoadSessionsAsync(token);
            var targets = sessions.Where(s => sessionId is not null ? s.Id == sessionId : s.WorkId == workId).ToArray();
            using var mutation = Ownership.BeginMutation(tabId, targets.Select(s => s.Id));
            if (workId is not null) await _store.DeleteWorkAsync(workId, token);
            else if (sessionId is not null) await _store.DeleteSessionAsync(sessionId, token);
            lock (_gate)
                foreach (var target in targets)
                {
                    _runtimes.Remove(target.Id);
                    replace |= tab.Selected?.SessionId == target.Id;
                }
        }
        finally { _changes.Release(); }
        if (replace)
        {
            var navigation = await GetNavigationAsync(token);
            var candidates = navigation.Works.SelectMany(w => w.Sessions).Concat(navigation.StandaloneSessions)
                .OrderByDescending(s => s.UpdatedAtUtc);
            foreach (var candidate in candidates)
            {
                try { if (await SelectAsync(tabId, candidate.Id, token)) { Changed?.Invoke(this, EventArgs.Empty); return; } }
                catch (SessionInUseException) { }
            }
            await CreateSessionAsync(tabId, null, token: token);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CloseTabAsync(TabId tabId)
    {
        WorkspaceTab tab;
        Task[] navigations;
        SessionRuntime[] running;
        await _changes.WaitAsync();
        try
        {
            lock (_gate)
            {
                tab = GetTab(tabId);
                tab.IsClosing = true;
                Ownership.BeginClose(tabId);
                tab.Lifetime.Cancel();
                navigations = tab.Navigations.ToArray();
                running = _runtimes.Values.Where(r => r.Owner == tabId && r.Runtime.IsRunning).Select(r => r.Runtime).ToArray();
                foreach (var runtime in running) runtime.Cancel();
            }
        }
        finally { _changes.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        try { await Task.WhenAll(navigations); }
        catch (OperationCanceledException) { }
        await Task.WhenAll(running.Select(r => r.Completion));
        lock (_gate)
        {
            Ownership.CompleteClose(tabId);
            _tabs.Remove(tab);
            foreach (var id in _runtimes.Where(r => r.Value.Owner == tabId).Select(r => r.Key).ToArray())
                _runtimes.Remove(id);
            if (_active == tabId) _active = _tabs.FirstOrDefault()?.Id;
            tab.Lifetime.Dispose();
        }
        await SaveActivePointerAsync(CancellationToken.None);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CloseAsync()
    {
        Session? lastActive;
        lock (_gate)
        {
            _closing = true;
            lastActive = _tabs.FirstOrDefault(t => t.Id == _active)?.Selected?.Controller.Session;
            foreach (var tab in _tabs) tab.IsClosing = true;
        }
        foreach (var tab in Tabs) await CloseTabAsync(tab.Id);
        if (lastActive is not null)
        {
            await _pointer.WaitAsync();
            try { await _store.SetCurrentAsync(lastActive); }
            finally { _pointer.Release(); }
        }
    }
}
