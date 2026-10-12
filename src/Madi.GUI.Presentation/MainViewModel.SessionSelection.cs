using Madi.App;

namespace Madi.GUI.Presentation;

public sealed partial class MainViewModel
{
    private readonly HashSet<string> _sessionsToDelete = new(StringComparer.Ordinal);
    private bool _isSelectingSessions;
    private bool _isDeletingSessions;

    public bool IsSelectingSessions => _isSelectingSessions;
    public bool CanChangeSessionSelection => CanNavigate && !_isDeletingSessions;
    public bool CanDeleteSelectedSessions => IsSelectingSessions && CanChangeSessionSelection && _sessionsToDelete.Count > 0;
    public string DeleteSelectedSessionsLabel => $"삭제 ({_sessionsToDelete.Count})";
    public int SelectedSessionCount => _sessionsToDelete.Count;

    public void SetSessionSelectionMode(bool enabled)
    {
        if (!CanChangeSessionSelection) return;
        _isSelectingSessions = enabled;
        _sessionsToDelete.Clear();
        UpdateSessionSelection();
        OnPropertyChanged(nameof(IsSelectingSessions));
    }

    public void SetSessionSelectedForDeletion(string sessionId, bool selected)
    {
        if (!IsSelectingSessions || !CanChangeSessionSelection
            || !NavigationItems.Any(x => x.SessionId == sessionId)) return;
        if (selected) _sessionsToDelete.Add(sessionId);
        else _sessionsToDelete.Remove(sessionId);
        UpdateSessionSelection();
    }

    private void UpdateSessionSelection()
    {
        for (var i = 0; i < NavigationItems.Count; i++)
        {
            var item = NavigationItems[i];
            var updated = item with
            {
                ShowsDeleteCheckbox = IsSelectingSessions && item.SessionId is not null,
                IsSelectedForDeletion = item.SessionId is { } id && _sessionsToDelete.Contains(id),
            };
            if (updated != item) NavigationItems[i] = updated;
        }
        NotifySessionSelection();
    }

    private void NotifySessionSelection()
    {
        OnPropertyChanged(nameof(CanChangeSessionSelection));
        OnPropertyChanged(nameof(CanDeleteSelectedSessions));
        OnPropertyChanged(nameof(DeleteSelectedSessionsLabel));
        OnPropertyChanged(nameof(SelectedSessionCount));
    }

    public async Task DeleteSelectedSessionsAsync()
    {
        if (!CanDeleteSelectedSessions || _tab is null) return;
        var tab = _tab;
        var ids = _sessionsToDelete.ToArray();
        _isDeletingSessions = true;
        NotifySessionSelection();
        try
        {
            await tab.Workspace.DeleteSessionsAsync(tab.Tab.Id, ids);
            await _dispatcher.InvokeAsync(() =>
            {
                _isDeletingSessions = false;
                SetSessionSelectionMode(false);
            });
        }
        catch (Exception error)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _isDeletingSessions = false;
                NotifySessionSelection();
            });
            await tab.SynchronizeAsync();
            if (tab.Current is { } current)
                await current.ApplyExternalEventAsync(new AppMessage(error.Message, AppMessageKind.Error));
        }
    }
}
