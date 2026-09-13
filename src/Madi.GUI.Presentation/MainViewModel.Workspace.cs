using Madi.App;

namespace Madi.GUI.Presentation;

public sealed partial class MainViewModel
{
    private WorkspaceTabViewModel? _tab;
    private WorkEditorState? _workEditor;
    private long _eventSequence;
    private long _navigationGeneration;
    private bool _visible = true;
    private bool _disposed;
    private string? _blockedSession;
    public SessionRuntime? Runtime { get; private set; }
    public double ScrollOffset { get; set; }
    public bool FollowsLatest { get; set; } = true;
    public string? CurrentSessionId => _controller.State.Session?.Id;
    public NavigationItemViewModel? SelectedNavigationItem => NavigationItems.FirstOrDefault(item => item.SessionId == CurrentSessionId);
    public bool CanNavigate => _tab?.Tab.IsClosing != true;
    public bool HasBlockedSession => _blockedSession is not null;
    public bool HasWorkConflict => _workEditor is { } editor && editor.Latest != editor.Baseline;
    public string LatestWorkPrompt => _workEditor?.Latest ?? CurrentWorkPrompt;

    public MainViewModel(WorkspaceTabViewModel tab, SessionRuntime runtime, IUiDispatcher dispatcher)
        : this(runtime.Controller, dispatcher)
    {
        _tab = tab; Runtime = runtime;
        ApplyState(runtime.Controller.State);
        ApplyConversation(runtime.InitialConversation);
        runtime.Changed += OnRuntimeChanged;
        DrainEvents();
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) DrainEvents();
    }

    private async void OnRuntimeChanged(object? sender, EventArgs args)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || Runtime is null) return;
            if (_visible) DrainEvents();
            IsBusy = Runtime.IsRunning;
        });
    }

    private void DrainEvents()
    {
        if (_disposed || Runtime is null) return;
        foreach (var entry in Runtime.EventsAfter(_eventSequence))
        {
            ApplyEvent(entry.Event);
            _eventSequence = entry.Sequence;
        }
        IsBusy = Runtime.IsRunning;
    }

    private void OnWorkEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(CurrentWorkPrompt)); OnPropertyChanged(nameof(WorkPromptDraft));
        OnPropertyChanged(nameof(CanSaveWorkPrompt)); OnPropertyChanged(nameof(HasWorkConflict));
        OnPropertyChanged(nameof(LatestWorkPrompt));
    }

    public async Task RefreshNavigationAsync()
    {
        if (_disposed || _tab is null) return;
        var generation = Interlocked.Increment(ref _navigationGeneration);
        var navigation = await _tab.Workspace.GetNavigationAsync();
        await _dispatcher.InvokeAsync(() => { if (!_disposed && generation == _navigationGeneration) ApplyNavigation(navigation); });
    }

    private async Task ExecuteWorkspaceAsync(AppAction action)
    {
        try { await _tab!.Workspace.ExecuteAsync(_tab.Tab.Id, action); }
        catch (SessionInUseException conflict) { await ShowBlockedAsync(conflict); }
        catch (Exception error) { await ApplyExternalEventAsync(new AppMessage(error.Message, AppMessageKind.Error)); }
    }

    public async Task ShowBlockedAsync(SessionInUseException conflict)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            _blockedSession = conflict.SessionId;
            ErrorText = $"{_tab!.Workspace.GetTab(conflict.Owner).Name}에서 사용 중인 세션입니다";
            OnPropertyChanged(nameof(HasBlockedSession));
        });
    }

    public async Task GoToOwnerAsync()
    {
        if (_blockedSession is { } session && _tab is not null)
        {
            try { await _tab.Workspace.GoToOwnerAsync(session); ClearError(); }
            catch (Exception error) { await ApplyExternalEventAsync(new AppMessage(error.Message, AppMessageKind.Error)); }
        }
    }

    private async Task SaveWorkspaceWorkAsync()
    {
        if (_tab is null || CurrentWorkId is null || _workEditor is null) return;
        var editor = _workEditor; var submittedDraft = editor.Draft; var prompt = submittedDraft.Trim();
        try
        {
            await _tab.Workspace.SaveWorkAsync(_tab.Tab.Id, CurrentWorkId, editor.Baseline, prompt);
            await _dispatcher.InvokeAsync(() => editor.CompleteSave(submittedDraft, prompt));
        }
        catch (WorkConflictException conflict)
        {
            await _dispatcher.InvokeAsync(() => { editor.Receive(conflict.Latest.Prompt); ErrorText = conflict.Message; });
        }
        catch (Exception error) { await ApplyExternalEventAsync(new AppMessage(error.Message, AppMessageKind.Error)); }
    }

    public void UseLatestWorkAsBaseline() => _workEditor?.Rebase();
}
