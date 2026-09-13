using System.Collections.ObjectModel;
using Madi.App;
using Madi.GUI.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;

namespace Madi.GUI;

public sealed partial class MainPage : Page
{
    private readonly GuiSettingsStore _settings = new();
    private readonly ObservableCollection<string> _recentWorkspaces = [];
    private readonly Dictionary<TabId, (WorkspaceTabViewModel View, WorkspaceTabPage Page, TabViewItem Item)> _views = [];
    private MadiAppHost? _host;
    private CancellationTokenSource? _workspaceCancellation;
    private Task? _utilityTask;
    private bool _synchronizing;
    private bool _closing;

    public MainPage()
    {
        InitializeComponent();
        RecentWorkspaceList.ItemsSource = _recentWorkspaces;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        await LoadRecentWorkspacesAsync();
    }
    private async Task LoadRecentWorkspacesAsync()
    {
        try
        {
            _recentWorkspaces.Clear();
            foreach (var path in await _settings.LoadRecentWorkspacesAsync()) _recentWorkspaces.Add(path);
            if (_recentWorkspaces.Count > 0) RecentWorkspaceList.SelectedIndex = 0;
        }
        catch (Exception error) { ShowError(error.Message); }
    }
    private async void OnBrowseWorkspace(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) await OpenWorkspaceAsync(folder.Path);
        }
        catch (Exception error) { ShowError(error.Message); }
    }
    private async void OnOpenRecentWorkspace(object sender, RoutedEventArgs args)
    {
        if (RecentWorkspaceList.SelectedItem is string path) await OpenWorkspaceAsync(path);
    }
    private async void OnRecentWorkspaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (RecentWorkspaceList.SelectedItem is string path) await OpenWorkspaceAsync(path);
    }
    private async Task OpenWorkspaceAsync(string path)
    {
        WorkspacePanel.IsHitTestVisible = false;
        try
        {
            if (!Directory.Exists(path)) throw new InvalidOperationException("선택한 폴더가 존재하지 않습니다.");
            var host = await MadiAppFactory.CreateAsync(workingDirectory: path);
            await host.Workspace.InitializeAsync();
            _host = host;
            _workspaceCancellation = new CancellationTokenSource();
            await _settings.AddRecentWorkspaceAsync(path);
            host.Workspace.Changed += OnWorkspaceChanged;
            await SynchronizeTabsAsync();
            WorkspacePanel.Visibility = Visibility.Collapsed;
            WorkspaceShell.Visibility = Visibility.Visible;
            _utilityTask = RunUtilityInstallerAsync(host, _workspaceCancellation.Token);
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { WorkspacePanel.IsHitTestVisible = true; }
    }
    private async Task RunUtilityInstallerAsync(MadiAppHost host, CancellationToken token)
    {
        try
        {
            await foreach (var item in host.UtilityInstaller.EnsureAsync(token))
                foreach (var view in _views.Values.ToArray())
                    if (view.View.Current is { } current) await current.ApplyExternalEventAsync(item);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { ShowError($"유틸리티 준비 실패: {error.Message}"); }
    }
    private async void OnWorkspaceChanged(object? sender, EventArgs args)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnWorkspaceChanged(sender, args)); return;
        }
        try { await SynchronizeTabsAsync(); }
        catch (Exception error) { ShowError(error.Message); }
    }
    private async Task SynchronizeTabsAsync()
    {
        if (_host is null || _synchronizing) return;
        _synchronizing = true;
        try
        {
            var workspace = _host.Workspace;
            foreach (var tab in workspace.Tabs.Where(t => t.Selected is not null))
            {
                if (!_views.TryGetValue(tab.Id, out var existing))
                {
                    var view = new WorkspaceTabViewModel(workspace, tab, new DispatcherQueueAdapter(DispatcherQueue));
                    await view.SynchronizeAsync();
                    var page = new WorkspaceTabPage(); page.Bind(view);
                    page.ChangeWorkspaceRequested += OnChangeWorkspace;
                    page.OpenInNewTabRequested += OnOpenInNewTab;
                    var item = new TabViewItem { Header = view.Header, Content = page, Tag = tab.Id,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
                    existing = (view, page, item);
                    _views.Add(tab.Id, existing); Tabs.TabItems.Add(item);
                }
                existing.Item.Header = existing.View.Header;
                existing.Item.IsClosable = !tab.IsClosing;
                existing.Page.IsEnabled = !tab.IsClosing;
            }
            foreach (var id in _views.Keys.Where(id => !workspace.Tabs.Any(t => t.Id == id)).ToArray())
            {
                var entry = _views[id]; entry.Page.Detach(); entry.View.Dispose();
                entry.Page.ChangeWorkspaceRequested -= OnChangeWorkspace;
                entry.Page.OpenInNewTabRequested -= OnOpenInNewTab;
                Tabs.TabItems.Remove(entry.Item); _views.Remove(id);
            }
            if (workspace.ActiveTab is { } active && _views.TryGetValue(active, out var selected)) Tabs.SelectedItem = selected.Item;
        }
        finally { _synchronizing = false; }
    }
    private async void OnAddTab(TabView sender, object args)
    {
        if (_closing || _host?.Workspace.ActiveTab is not { } active) return;
        try { await _host.Workspace.NewTabAsync(active); await SynchronizeTabsAsync(); }
        catch (Exception error) { ShowError(error.Message); }
    }
    private async void OnOpenInNewTab(object? sender, string session)
    {
        if (_closing || _host?.Workspace.ActiveTab is not { } active) return;
        try { await _host.Workspace.NewTabAsync(active, session); await SynchronizeTabsAsync(); }
        catch (SessionInUseException conflict)
        {
            if (_views.GetValueOrDefault(active).View?.Current is { } current) await current.ShowBlockedAsync(conflict);
        }
        catch (Exception error) { ShowError(error.Message); }
    }
    private async void OnTabSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_synchronizing || _host is null || Tabs.SelectedItem is not TabViewItem { Tag: TabId id }) return;
        try { await _host.Workspace.ActivateTabAsync(id); }
        catch (Exception error) { ShowError(error.Message); }
    }
    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (_closing || _host is null || args.Tab.Tag is not TabId id) return;
        _closing = true;
        try
        {
            if (!await ConfirmCloseAsync([id])) return;
            var workspace = _host.Workspace;
            var last = workspace.Tabs.Count == 1;
            // Prepare the replacement first, preserving history and monotonically increasing tab numbers.
            if (last) await workspace.NewTabAsync(id);
            await workspace.CloseTabAsync(id);
            await SynchronizeTabsAsync();
        }
        catch (Exception error) { ShowError($"탭을 닫지 못했습니다: {error.Message}"); }
        finally { _closing = false; }
    }
    private async Task<bool> ConfirmCloseAsync(IEnumerable<TabId> ids)
    {
        if (_host is null) return true;
        var tabs = ids.Select(id => _views.GetValueOrDefault(id).View).Where(v => v is not null).ToArray();
        var running = tabs.Sum(t => _host.Workspace.RunningCount(t.Tab.Id));
        var dirty = tabs.Any(t => t.HasUnsavedChanges);
        if (running == 0 && !dirty) return true;
        var descriptions = tabs.Select(t => $"{t.Tab.Name}: 실행/저장 {_host.Workspace.RunningCount(t.Tab.Id)}개"
            + (running > 0 ? " · " + string.Join(", ", _host.Workspace.RunningTitles(t.Tab.Id)) : "")
            + (t.HasUnsavedChanges ? ", 미전송 초안 또는 미저장 Work 편집 있음" : ""));
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "탭을 닫을까요?",
            Content = new TextBlock { Text = string.Join("\n", descriptions) + "\n\n"
                + (dirty ? "미전송 초안과 미저장 Work 편집을 버립니다.\n" : "")
                + (running > 0 ? "해당 탭의 작업을 중지하고 저장과 정리가 끝날 때까지 기다립니다." : ""), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = running > 0 ? "작업 중지 후 닫기" : "닫기", CloseButtonText = "취소", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    public async Task<bool> TryCloseAsync()
    {
        if (_closing) return false;
        _closing = true;
        try
        {
            if (_host is null) return true;
            if (!await ConfirmCloseAsync(_host.Workspace.Tabs.Select(t => t.Id))) return false;
            Tabs.IsEnabled = false;
            await _host.Workspace.CloseAsync();
            _workspaceCancellation?.Cancel();
            if (_utilityTask is not null) await _utilityTask;
            _host.Workspace.Changed -= OnWorkspaceChanged;
            foreach (var entry in _views.Values) { entry.Page.Detach(); entry.View.Dispose(); }
            _views.Clear(); Tabs.TabItems.Clear(); _host = null;
            _workspaceCancellation?.Dispose(); _workspaceCancellation = null;
            return true;
        }
        catch (Exception error) { ShowError($"정리를 완료하지 못했습니다: {error.Message}"); return false; }
        finally { _closing = false; Tabs.IsEnabled = true; }
    }
    private async void OnChangeWorkspace(object? sender, EventArgs args)
    {
        if (!await TryCloseAsync()) return;
        WorkspaceShell.Visibility = Visibility.Collapsed;
        WorkspacePanel.Visibility = Visibility.Visible;
        await LoadRecentWorkspacesAsync();
    }
    private void ShowError(string message)
    {
        WorkspaceError.Message = message; WorkspaceError.IsOpen = true;
        ShellError.Message = message; ShellError.IsOpen = true;
    }
}
