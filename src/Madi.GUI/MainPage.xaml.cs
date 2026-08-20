using System.Collections.ObjectModel;
using Madi.App;
using Madi.GUI.Presentation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Madi.GUI;

public sealed partial class MainPage : Page
{
    private readonly GuiSettingsStore _settings = new();
    private readonly ObservableCollection<string> _recentWorkspaces = [];
    private MainViewModel? _viewModel;
    private CancellationTokenSource? _workspaceCancellation;
    private ScrollViewer? _timelineScroller;

    public MainPage()
    {
        InitializeComponent();
        RecentWorkspaceList.ItemsSource = _recentWorkspaces;
        Loaded += OnLoaded;
    }

    public void CancelAndDispose()
    {
        _workspaceCancellation?.Cancel();
        _workspaceCancellation?.Dispose();
        _workspaceCancellation = null;
        _viewModel?.Cancel();
        _viewModel?.Dispose();
        _viewModel = null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await LoadRecentWorkspacesAsync();
        _timelineScroller = FindVisualChild<ScrollViewer>(TimelineList);
        if (_timelineScroller is not null)
        {
            _timelineScroller.ViewChanged += OnTimelineViewChanged;
        }
    }

    private async Task LoadRecentWorkspacesAsync()
    {
        try
        {
            _recentWorkspaces.Clear();
            foreach (var path in await _settings.LoadRecentWorkspacesAsync())
            {
                _recentWorkspaces.Add(path);
            }
            if (_recentWorkspaces.Count > 0)
            {
                RecentWorkspaceList.SelectedIndex = 0;
            }
        }
        catch (Exception exception)
        {
            ShowWorkspaceError($"최근 작업공간을 불러오지 못했습니다: {exception.Message}");
        }
    }

    private async void OnBrowseWorkspace(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                await OpenWorkspaceAsync(folder.Path);
            }
        }
        catch (Exception exception)
        {
            ShowWorkspaceError($"폴더를 열지 못했습니다: {exception.Message}");
        }
    }

    private async void OnOpenRecentWorkspace(object sender, RoutedEventArgs e)
    {
        if (RecentWorkspaceList.SelectedItem is string path)
        {
            await OpenWorkspaceAsync(path);
        }
    }

    private async void OnRecentWorkspaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RecentWorkspaceList.SelectedItem is string path)
        {
            await OpenWorkspaceAsync(path);
        }
    }

    private async Task OpenWorkspaceAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            ShowWorkspaceError("선택한 폴더가 존재하지 않습니다.");
            return;
        }

        WorkspaceError.IsOpen = false;
        WorkspacePanel.IsHitTestVisible = false;
        try
        {
            await _settings.AddRecentWorkspaceAsync(path);
            var host = await MadiAppFactory.CreateAsync(workingDirectory: path);
            _workspaceCancellation?.Cancel();
            _workspaceCancellation?.Dispose();
            _workspaceCancellation = new CancellationTokenSource();
            _viewModel?.Dispose();
            _viewModel = new MainViewModel(
                host.Controller,
                new DispatcherQueueAdapter(DispatcherQueue));
            _viewModel.TimelineChanged += OnTimelineChanged;
            _viewModel.ExitRequested += OnExitRequested;
            DataContext = _viewModel;
            await _viewModel.InitializeAsync(_workspaceCancellation.Token);

            WorkspacePanel.Visibility = Visibility.Collapsed;
            Shell.Visibility = Visibility.Visible;
            LegacyInfoBar.IsOpen = _viewModel.LegacyItemCount > 0;
            PromptBox.Focus(FocusState.Programmatic);
            _ = RunUtilityInstallerAsync(
                host.UtilityInstaller,
                _viewModel,
                _workspaceCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowWorkspaceError($"작업공간을 초기화하지 못했습니다: {exception.Message}");
        }
        finally
        {
            WorkspacePanel.IsHitTestVisible = true;
        }
    }

    private static async Task RunUtilityInstallerAsync(
        UtilityInstaller installer,
        MainViewModel viewModel,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in installer.EnsureAsync(cancellationToken))
            {
                await viewModel.ApplyExternalEventAsync(item);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await viewModel.ApplyExternalEventAsync(new AppMessage(
                $"필수 유틸리티를 준비하지 못했습니다: {exception.Message}",
                AppMessageKind.Warning));
        }
    }

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        if (_viewModel?.FollowLatest == true && _viewModel.Timeline.LastOrDefault() is { } last)
        {
            TimelineList.UpdateLayout();
            TimelineList.ScrollIntoView(last);
        }
    }

    private void OnTimelineViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_viewModel is null || _timelineScroller is null)
        {
            return;
        }

        var atBottom = _timelineScroller.ScrollableHeight - _timelineScroller.VerticalOffset < 24;
        _viewModel.FollowLatest = atBottom;
        LatestButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnGoLatest(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.Timeline.LastOrDefault() is { } last)
        {
            _viewModel.FollowLatest = true;
            TimelineList.ScrollIntoView(last);
            LatestButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnNavigationItemClick(object sender, ItemClickEventArgs e)
    {
        if (_viewModel is not null
            && e.ClickedItem is NavigationItemViewModel { IsHeader: false, SessionId: { } id })
        {
            await _viewModel.ActivateSessionAsync(id);
        }
    }

    private void OnSessionMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                DataContext: NavigationItemViewModel
                {
                    IsHeader: false,
                    SessionId: not null,
                } session,
            } button)
        {
            return;
        }

        var deleteItem = new MenuFlyoutItem
        {
            Text = "대화 삭제",
            Icon = new FontIcon { Glyph = "\uE74D" },
            Tag = session,
        };
        deleteItem.Click += OnDeleteSession;

        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(button);
    }

    private async void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || sender is not MenuFlyoutItem
            {
                Tag: NavigationItemViewModel { SessionId: { } sessionId } session,
            })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "대화를 삭제할까요?",
            Content = new TextBlock
            {
                Text = $"‘{session.Title}’ 대화와 모든 기록이 영구적으로 삭제됩니다.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _viewModel.DeleteSessionAsync(sessionId);
        }
    }

    private async void OnNewSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.CreateSessionAsync(_viewModel.CurrentWorkId);
            PromptBox.Focus(FocusState.Programmatic);
        }
    }

    private async void OnNewWork(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var input = new TextBox
        {
            PlaceholderText = "Work 제목",
            MinWidth = 320,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "새 Work",
            Content = input,
            PrimaryButtonText = "만들기",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && !string.IsNullOrWhiteSpace(input.Text))
        {
            await _viewModel.CreateWorkAsync(input.Text);
        }
    }

    private async void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && ModelSelector.SelectedValue is string model)
        {
            await _viewModel.SelectModelAsync(model);
        }
    }

    private async void OnToggleAuthentication(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.ToggleAuthenticationAsync();
        }
    }

    private async void OnSendOrCancel(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.SendOrCancelAsync();
        }
    }

    private async void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.Key == VirtualKey.Tab && _viewModel.Suggestions.FirstOrDefault() is { } first)
        {
            _viewModel.CompleteSuggestion(first);
            PromptBox.SelectionStart = PromptBox.Text.Length;
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && _viewModel.Suggestions.Count > 0)
        {
            _viewModel.Suggestions.Clear();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter && IsModifierDown(VirtualKey.Control)
                                     && !IsModifierDown(VirtualKey.Shift)
                                     && !IsModifierDown(VirtualKey.Menu))
        {
            e.Handled = true;
            await _viewModel.SendOrCancelAsync();
        }
    }

    private void OnSuggestionDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_viewModel is not null
            && SuggestionList.SelectedItem is SuggestionItemViewModel suggestion)
        {
            _viewModel.CompleteSuggestion(suggestion);
            PromptBox.SelectionStart = PromptBox.Text.Length;
            PromptBox.Focus(FocusState.Programmatic);
        }
    }

    private async void OnImportLegacyData(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.ImportLegacyDataAsync();
            LegacyInfoBar.IsOpen = false;
        }
    }

    private async void OnChangeWorkspace(object sender, RoutedEventArgs e)
    {
        CancelAndDispose();
        DataContext = null;
        Shell.Visibility = Visibility.Collapsed;
        WorkspacePanel.Visibility = Visibility.Visible;
        await LoadRecentWorkspacesAsync();
    }

    private void OnErrorClosed(InfoBar sender, object args) =>
        _viewModel?.ClearError();

    private void OnExitRequested(object? sender, EventArgs e) => App.Window.Close();

    private void ShowWorkspaceError(string message)
    {
        WorkspaceError.Message = message;
        WorkspaceError.IsOpen = true;
    }

    private static bool IsModifierDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }
}
