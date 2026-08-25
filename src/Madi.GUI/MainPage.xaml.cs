using System.Collections.ObjectModel;
using Madi.App;
using Madi.GUI.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace Madi.GUI;

public sealed partial class MainPage : Page
{
    private readonly GuiSettingsStore _settings = new();
    private readonly ObservableCollection<string> _recentWorkspaces = [];
    private MainViewModel? _viewModel;
    private CancellationTokenSource? _workspaceCancellation;
    private bool _isTimelineScrollPending;
    private bool _isAutoScrollingTimeline;

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
            var openedViewModel = _viewModel;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_viewModel, openedViewModel))
                {
                    openedViewModel.FollowLatest = true;
                    ScrollTimelineToEnd();
                }
            });
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
        if (_viewModel?.FollowLatest == true)
        {
            ScrollTimelineToEnd();
        }
    }

    private void OnTimelineViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var atBottom = IsTimelineAtBottom();
        if (!_isAutoScrollingTimeline)
        {
            _viewModel.FollowLatest = atBottom;
        }
        LatestButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnGoLatest(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.FollowLatest = true;
        ScrollTimelineToEnd();
        LatestButton.Visibility = Visibility.Collapsed;
    }

    private void ScrollTimelineToEnd()
    {
        if (_isTimelineScrollPending)
        {
            return;
        }

        _isTimelineScrollPending = true;
        _isAutoScrollingTimeline = true;
        if (!DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ApplyTimelineScrollToEnd(verifyAfterLayout: true)))
        {
            _isTimelineScrollPending = false;
            _isAutoScrollingTimeline = false;
        }
    }

    private void ApplyTimelineScrollToEnd(bool verifyAfterLayout)
    {
        if (_viewModel?.FollowLatest != true)
        {
            _isTimelineScrollPending = false;
            _isAutoScrollingTimeline = false;
            return;
        }

        TimelineScroller.UpdateLayout();
        TimelineScroller.ChangeView(
            horizontalOffset: null,
            verticalOffset: TimelineScroller.ScrollableHeight,
            zoomFactor: null,
            disableAnimation: true);

        // Markdown rendering changes the item height after its Text binding has updated.
        // Run once more on the next layout pass so ChangeView does not use a stale extent.
        if (verifyAfterLayout && DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ApplyTimelineScrollToEnd(verifyAfterLayout: false)))
        {
            return;
        }

        if (DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                CompleteTimelineScrollToEnd))
        {
            return;
        }

        CompleteTimelineScrollToEnd();
    }

    private void CompleteTimelineScrollToEnd()
    {
        _isTimelineScrollPending = false;
        _isAutoScrollingTimeline = false;
        LatestButton.Visibility = IsTimelineAtBottom()
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private bool IsTimelineAtBottom() =>
        TimelineScroller.ScrollableHeight - TimelineScroller.VerticalOffset < 24;

    private void OnCopyAssistantMarkdown(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                DataContext: TimelineItemViewModel
                {
                    Kind: TimelineItemKind.Assistant,
                    Text: var markdown,
                },
            })
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(markdown);
        Clipboard.SetContent(package);
    }

    private void OnToggleActivityDetails(object sender, RoutedEventArgs e)
    {
        if (sender is Button
            {
                DataContext: TimelineItemViewModel { IsActivity: true } activity,
            })
        {
            activity.IsActivityExpanded = !activity.IsActivityExpanded;
        }
    }

    private void OnTimelinePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        const double lineScrollAmount = 48;
        var pageScrollAmount = Math.Max(
            lineScrollAmount,
            TimelineScroller.ViewportHeight - lineScrollAmount);
        var targetOffset = e.Key switch
        {
            VirtualKey.Up => TimelineScroller.VerticalOffset - lineScrollAmount,
            VirtualKey.Down => TimelineScroller.VerticalOffset + lineScrollAmount,
            VirtualKey.PageUp => TimelineScroller.VerticalOffset - pageScrollAmount,
            VirtualKey.PageDown => TimelineScroller.VerticalOffset + pageScrollAmount,
            VirtualKey.Home => 0,
            VirtualKey.End => TimelineScroller.ScrollableHeight,
            _ => double.NaN,
        };
        if (double.IsNaN(targetOffset))
        {
            return;
        }

        targetOffset = Math.Clamp(targetOffset, 0, TimelineScroller.ScrollableHeight);
        TimelineScroller.ChangeView(
            horizontalOffset: null,
            verticalOffset: targetOffset,
            zoomFactor: null,
            disableAnimation: false);
        e.Handled = true;
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

    private void OnNewSessionMenu(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button button)
        {
            return;
        }

        var menu = new MenuFlyout();
        if (_viewModel.HasCurrentWork)
        {
            var currentWorkItem = new MenuFlyoutItem
            {
                Text = "현재 Work에 새 대화",
            };
            currentWorkItem.Click += OnNewSessionInCurrentWork;
            menu.Items.Add(currentWorkItem);
        }

        var standaloneItem = new MenuFlyoutItem
        {
            Text = "독립 대화",
        };
        standaloneItem.Click += OnNewStandaloneSession;
        menu.Items.Add(standaloneItem);
        menu.ShowAt(button);
    }

    private async void OnNewSessionInCurrentWork(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.CurrentWorkId is { } workId)
        {
            await _viewModel.CreateSessionAsync(workId);
            PromptBox.Focus(FocusState.Programmatic);
        }
    }

    private async void OnNewStandaloneSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.CreateSessionAsync(workId: null);
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

    private async void OnSaveWorkPrompt(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.SaveWorkPromptAsync();
        }
    }

    private void OnLeftSplitterDragDelta(object sender, DragDeltaEventArgs e) =>
        ResizePanel(
            SidebarColumn,
            SidebarColumn.ActualWidth + e.HorizontalChange,
            WorkPanelColumn.ActualWidth);

    private void OnRightSplitterDragDelta(object sender, DragDeltaEventArgs e) =>
        ResizePanel(
            WorkPanelColumn,
            WorkPanelColumn.ActualWidth - e.HorizontalChange,
            SidebarColumn.ActualWidth);

    private void ResizePanel(ColumnDefinition column, double requestedWidth, double oppositeWidth)
    {
        const double splitterWidth = 12;
        const double minimumConversationWidth = 360;

        var availableWidth = Shell.ActualWidth
            - oppositeWidth
            - splitterWidth
            - minimumConversationWidth;
        var maximumWidth = Math.Min(column.MaxWidth, availableWidth);
        if (maximumWidth < column.MinWidth)
        {
            return;
        }

        column.Width = new GridLength(Math.Clamp(
            requestedWidth,
            column.MinWidth,
            maximumWidth));
    }

    private async void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && ModelSelector.SelectedValue is string model)
        {
            await _viewModel.SelectModelAsync(model);
        }
    }

    private void OnToolsMenu(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button button)
        {
            return;
        }

        var systemPromptItem = new MenuFlyoutItem
        {
            Text = "시스템 프롬프트 보기",
            Icon = new FontIcon { Glyph = "\uE8A5" },
        };
        systemPromptItem.Click += OnShowSystemPrompt;

        var menu = new MenuFlyout();
        menu.Items.Add(systemPromptItem);
        menu.ShowAt(button);
    }

    private async void OnShowSystemPrompt(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var prompt = _viewModel.SystemInstructions;
        var content = new Grid
        {
            Width = Math.Clamp(ActualWidth - 160, 400, 760),
            Height = Math.Clamp(ActualHeight - 180, 300, 520),
            RowSpacing = 8,
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        content.Children.Add(new TextBlock
        {
            Text = "모든 대화에서 모델에 전달되는 현재 Agent instruction입니다. SYSTEM.md와 현재 도구 정보로 생성됩니다.",
            TextWrapping = TextWrapping.Wrap,
        });
        var promptBox = new TextBox
        {
            Text = prompt,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(promptBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(promptBox, ScrollBarVisibility.Auto);
        Grid.SetRow(promptBox, 1);
        content.Children.Add(promptBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "실제 시스템 프롬프트",
            Content = content,
            PrimaryButtonText = "복사",
            CloseButtonText = "닫기",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(prompt);
            Clipboard.SetContent(package);
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

    private void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
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

    }

    private async void OnPromptSendAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        e.Handled = true;
        await _viewModel.SendOrCancelAsync();
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

}
