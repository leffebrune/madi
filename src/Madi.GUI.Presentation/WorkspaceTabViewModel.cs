using Madi.App;

namespace Madi.GUI.Presentation;

public sealed class WorkEditorState : ObservableObject
{
    private string _baseline;
    private string _draft;
    private string _latest;
    public WorkEditorState(string prompt) { _baseline = _draft = _latest = prompt; }
    public string Baseline => _baseline;
    public string Latest => _latest;
    public string Draft { get => _draft; set { if (SetProperty(ref _draft, value)) OnPropertyChanged(nameof(IsDirty)); } }
    public bool IsDirty => Draft != Baseline;
    public void Receive(string prompt)
    {
        var dirty = IsDirty;
        _latest = prompt;
        if (!dirty) { _baseline = prompt; Draft = prompt; }
        OnPropertyChanged(nameof(Baseline)); OnPropertyChanged(nameof(Latest)); OnPropertyChanged(nameof(IsDirty));
    }
    public void CompleteSave(string submittedDraft, string savedPrompt)
    {
        _baseline = savedPrompt;
        // A later broadcast may already describe a newer save by another tab.
        if (Draft == submittedDraft) Draft = savedPrompt;
        OnPropertyChanged(nameof(Baseline)); OnPropertyChanged(nameof(IsDirty));
    }
    public void Rebase() { _baseline = _latest; OnPropertyChanged(nameof(Baseline)); OnPropertyChanged(nameof(IsDirty)); }
}

public sealed class WorkspaceTabViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, (SessionRuntime Runtime, MainViewModel View)> _sessions = [];
    private readonly Dictionary<string, WorkEditorState> _workEditors = [];
    private MainViewModel? _current;
    private bool _disposed;
    public WorkspaceTabViewModel(WorkspaceCoordinator workspace, WorkspaceTab tab, IUiDispatcher dispatcher)
    {
        Workspace = workspace; Tab = tab; _dispatcher = dispatcher;
        workspace.Changed += OnChanged;
        workspace.WorkChanged += OnWorkChanged;
    }
    public WorkspaceCoordinator Workspace { get; }
    public WorkspaceTab Tab { get; }
    public MainViewModel? Current { get => _current; private set => SetProperty(ref _current, value); }
    public string Header => Tab.Name + (Workspace.RunningCount(Tab.Id) is > 0 and var count ? $" · 실행 {count}" : "")
        + (Workspace.HasUnseenResult(Tab.Id) ? " · ● 미확인" : "") + (Tab.IsClosing ? " · 정리 중" : "");
    public bool HasUnsavedChanges => _sessions.Values.Any(s => s.View.Draft.Length > 0)
        || _workEditors.Values.Any(w => w.IsDirty);
    public double LeftWidth { get; set; } = 280;
    public double RightWidth { get; set; } = 300;
    public event EventHandler? CurrentChanged;

    internal WorkEditorState Editor(WorkSnapshot work)
    {
        if (!_workEditors.TryGetValue(work.Id, out var editor))
            _workEditors.Add(work.Id, editor = new WorkEditorState(work.Prompt));
        return editor;
    }

    public async Task SynchronizeAsync()
    {
        if (_disposed) return;
        await _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(Header));
            if (Tab.Selected is not { } runtime) return;
            if (Current?.Runtime == runtime) { Current.SetVisible(Workspace.ActiveTab == Tab.Id); return; }
            Current?.SetVisible(false);
            if (!_sessions.TryGetValue(runtime.SessionId, out var cached) || cached.Runtime != runtime)
            {
                var view = new MainViewModel(this, runtime, _dispatcher);
                if (cached.View is { } old)
                {
                    view.Draft = old.Draft; view.ScrollOffset = old.ScrollOffset; view.FollowsLatest = old.FollowsLatest;
                    old.Dispose();
                }
                _sessions[runtime.SessionId] = (runtime, view);
                cached = (runtime, view);
            }
            Current = cached.View;
            Current.SetVisible(Workspace.ActiveTab == Tab.Id);
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        });
        if (Current is { } current) await current.RefreshNavigationAsync();
    }

    private async void OnChanged(object? sender, EventArgs args)
    {
        try
        {
            await SynchronizeAsync();
            if (Current is { } current) await current.RefreshNavigationAsync();
        }
        catch (Exception error) { if (Current is { } current) await current.ApplyExternalEventAsync(new AppMessage(error.Message, AppMessageKind.Error)); }
    }

    private async void OnWorkChanged(object? sender, WorkSnapshot work)
    {
        await _dispatcher.InvokeAsync(() => { if (!_disposed && _workEditors.TryGetValue(work.Id, out var editor)) editor.Receive(work.Prompt); });
    }

    public void Dispose()
    {
        _disposed = true;
        Workspace.Changed -= OnChanged; Workspace.WorkChanged -= OnWorkChanged;
        foreach (var session in _sessions.Values) session.View.Dispose();
        _sessions.Clear();
    }
}
