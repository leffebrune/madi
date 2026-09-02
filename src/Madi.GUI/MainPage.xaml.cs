using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    private enum TimelineScrollMode
    {
        FollowLatest,
        Free,
    }

    private const double TimelineBottomTolerance = 0.5;
    private readonly GuiSettingsStore _settings = new();
    private readonly ObservableCollection<string> _recentWorkspaces = [];
    private readonly HashSet<TimelineItemViewModel> _observedTimelineItems = [];
    private MainViewModel? _viewModel;
    private CancellationTokenSource? _workspaceCancellation;
    private Control? _systemPromptPreviousFocus;
    private TimelineScrollMode _timelineScrollMode = TimelineScrollMode.FollowLatest;
    private bool _timelineScrollRequested;
    private bool _timelineScrollScheduled;
    private int _timelineScrollGeneration;

    public MainPage()
    {
        InitializeComponent();
        TimelineScroller.AddHandler(
            PointerWheelChangedEvent,
            new PointerEventHandler(OnTimelinePointerWheelChanged),
            handledEventsToo: true);
        RecentWorkspaceList.ItemsSource = _recentWorkspaces;
        Loaded += OnLoaded;
    }

    public void CancelAndDispose()
    {
        DetachTimeline();
        CancelTimelineScroll();
        _workspaceCancellation?.Cancel();
        _workspaceCancellation?.Dispose();
        _workspaceCancellation = null;
        if (_viewModel is not null)
        {
            _viewModel.ExitRequested -= OnExitRequested;
            _viewModel.Cancel();
            _viewModel.Dispose();
            _viewModel = null;
        }
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
            if (_viewModel is not null)
            {
                DetachTimeline();
                _viewModel.ExitRequested -= OnExitRequested;
                _viewModel.Dispose();
            }
            _viewModel = new MainViewModel(
                host.Controller,
                new DispatcherQueueAdapter(DispatcherQueue));
            AttachTimeline(_viewModel);
            _viewModel.ExitRequested += OnExitRequested;
            DataContext = _viewModel;
            await _viewModel.InitializeAsync(_workspaceCancellation.Token);

            WorkspacePanel.Visibility = Visibility.Collapsed;
            Shell.Visibility = Visibility.Visible;
            LegacyInfoBar.IsOpen = _viewModel.LegacyItemCount > 0;
            FollowLatestAndScroll();
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

    private void OnTimelinePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_timelineScrollMode == TimelineScrollMode.FollowLatest
            && e.GetCurrentPoint(TimelineScroller).Properties.MouseWheelDelta > 0)
        {
            SetTimelineScrollMode(TimelineScrollMode.Free);
        }
    }

    private void OnTimelinePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.End)
        {
            e.Handled = true;
            FollowLatestAndScroll();
            return;
        }

        if (_timelineScrollMode == TimelineScrollMode.FollowLatest
            && e.Key is VirtualKey.Up or VirtualKey.PageUp or VirtualKey.Home)
        {
            SetTimelineScrollMode(TimelineScrollMode.Free);
        }
    }

    private void OnGoLatest(object sender, RoutedEventArgs e) => FollowLatestAndScroll();

    private void FollowLatestAndScroll()
    {
        SetTimelineScrollMode(TimelineScrollMode.FollowLatest);
        RequestTimelineScrollToEnd(requireAgentBusy: false);
    }

    private void SetTimelineScrollMode(TimelineScrollMode mode)
    {
        if (_timelineScrollMode == mode)
        {
            LatestButton.Visibility = mode == TimelineScrollMode.Free
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        _timelineScrollMode = mode;
        if (mode == TimelineScrollMode.Free)
        {
            CancelTimelineScroll();
        }
        LatestButton.Visibility = mode == TimelineScrollMode.Free
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AttachTimeline(MainViewModel viewModel)
    {
        viewModel.Timeline.CollectionChanged += OnTimelineCollectionChanged;
        foreach (var item in viewModel.Timeline)
        {
            AttachTimelineItem(item);
        }
    }

    private void DetachTimeline()
    {
        if (_viewModel is not null)
        {
            _viewModel.Timeline.CollectionChanged -= OnTimelineCollectionChanged;
        }
        foreach (var item in _observedTimelineItems)
        {
            item.PropertyChanged -= OnTimelineItemPropertyChanged;
        }
        _observedTimelineItems.Clear();
    }

    private void OnTimelineCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<TimelineItemViewModel>())
            {
                DetachTimelineItem(item);
            }
        }
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<TimelineItemViewModel>())
            {
                AttachTimelineItem(item);
            }
        }
        if (e.Action == NotifyCollectionChangedAction.Reset && _viewModel is not null)
        {
            foreach (var item in _observedTimelineItems)
            {
                item.PropertyChanged -= OnTimelineItemPropertyChanged;
            }
            _observedTimelineItems.Clear();
            foreach (var item in _viewModel.Timeline)
            {
                AttachTimelineItem(item);
            }
        }

        RequestTimelineScrollToEnd(requireAgentBusy: true);
    }

    private void AttachTimelineItem(TimelineItemViewModel item)
    {
        if (_observedTimelineItems.Add(item))
        {
            item.PropertyChanged += OnTimelineItemPropertyChanged;
        }
    }

    private void DetachTimelineItem(TimelineItemViewModel item)
    {
        if (_observedTimelineItems.Remove(item))
        {
            item.PropertyChanged -= OnTimelineItemPropertyChanged;
        }
    }

    private void OnTimelineItemPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RequestTimelineScrollToEnd(requireAgentBusy: true);

    private void RequestTimelineScrollToEnd(bool requireAgentBusy)
    {
        if (_timelineScrollMode != TimelineScrollMode.FollowLatest
            || (requireAgentBusy && _viewModel?.IsBusy != true))
        {
            return;
        }

        _timelineScrollRequested = true;
        if (_timelineScrollScheduled)
        {
            return;
        }

        _timelineScrollScheduled = true;
        var generation = _timelineScrollGeneration;
        if (!DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ApplyTimelineScrollToEnd(generation)))
        {
            _timelineScrollScheduled = false;
            _timelineScrollRequested = false;
        }
    }

    private void ApplyTimelineScrollToEnd(int generation)
    {
        if (generation != _timelineScrollGeneration)
        {
            return;
        }

        _timelineScrollScheduled = false;
        if (_timelineScrollMode != TimelineScrollMode.FollowLatest
            || !_timelineScrollRequested)
        {
            return;
        }

        _timelineScrollRequested = false;
        TimelineScroller.UpdateLayout();
        var targetOffset = TimelineScroller.ScrollableHeight;
        if (targetOffset - TimelineScroller.VerticalOffset > TimelineBottomTolerance)
        {
            TimelineScroller.ChangeView(
                horizontalOffset: null,
                verticalOffset: targetOffset,
                zoomFactor: null,
                disableAnimation: true);
        }
    }

    private void CancelTimelineScroll()
    {
        _timelineScrollGeneration++;
        _timelineScrollRequested = false;
        _timelineScrollScheduled = false;
    }

    private void OnConversationItemMenuClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.CanInteract != true
            || sender is not Button
            {
                DataContext: TimelineItemViewModel { CanDeleteFromHere: true } item,
            } button)
        {
            return;
        }

        var deleteItem = new MenuFlyoutItem
        {
            Text = "여기서부터 삭제",
            Icon = new FontIcon { Glyph = "\uE74D" },
            Tag = item,
        };
        deleteItem.Click += OnDeleteConversationFrom;

        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(button);
    }

    private async void OnDeleteConversationFrom(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.CanInteract != true
            || sender is not MenuFlyoutItem
            {
                Tag: TimelineItemViewModel
                {
                    CanDeleteFromHere: true,
                    SessionId: { } sessionId,
                    ConversationIndex: { } conversationIndex,
                },
            })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "여기서부터 삭제할까요?",
            Content = new TextBlock
            {
                Text = "선택한 메시지와 그 이후의 모든 대화가 영구적으로 삭제됩니다.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _viewModel.DeleteConversationFromAsync(sessionId, conversationIndex);
        PromptBox.Focus(FocusState.Programmatic);
    }

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

    private async void OnNavigationItemClick(object sender, ItemClickEventArgs e)
    {
        if (_viewModel is not null
            && e.ClickedItem is NavigationItemViewModel { IsHeader: false, SessionId: { } id })
        {
            await _viewModel.ActivateSessionAsync(id);
            FollowLatestAndScroll();
        }
    }

    private void OnNavigationMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NavigationItemViewModel item } button)
        {
            return;
        }

        var isWork = item is { IsHeader: true, WorkId: not null };
        if (!isWork && item.SessionId is null)
        {
            return;
        }

        var deleteItem = new MenuFlyoutItem
        {
            Text = isWork ? "Work 삭제" : "대화 삭제",
            Icon = new FontIcon { Glyph = "\uE74D" },
            Tag = item,
        };
        deleteItem.Click += isWork ? OnDeleteWork : OnDeleteSession;

        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(button);
    }

    private async void OnNavigationListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete
            || NavigationList.SelectedItem is not NavigationItemViewModel { HasMenu: true } item)
        {
            return;
        }

        e.Handled = true;
        await ConfirmAndDeleteNavigationItemAsync(item);
    }

    private async void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem
            {
                Tag: NavigationItemViewModel { SessionId: not null } session,
            })
        {
            await ConfirmAndDeleteNavigationItemAsync(session);
        }
    }

    private async void OnDeleteWork(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem
            {
                Tag: NavigationItemViewModel { IsHeader: true, WorkId: not null } work,
            })
        {
            await ConfirmAndDeleteNavigationItemAsync(work);
        }
    }

    private async Task ConfirmAndDeleteNavigationItemAsync(NavigationItemViewModel item)
    {
        if (_viewModel is null)
        {
            return;
        }

        var isWork = item is { IsHeader: true, WorkId: not null };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isWork ? "Work를 삭제할까요?" : "대화를 삭제할까요?",
            Content = new TextBlock
            {
                Text = isWork
                    ? $"‘{item.Title}’ Work와 여기에 포함된 모든 대화가 영구적으로 삭제됩니다."
                    : $"‘{item.Title}’ 대화와 모든 기록이 영구적으로 삭제됩니다.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (isWork)
        {
            await _viewModel.DeleteWorkAsync(item.WorkId!);
        }
        else if (item.SessionId is { } sessionId)
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
            FollowLatestAndScroll();
            PromptBox.Focus(FocusState.Programmatic);
        }
    }

    private async void OnNewStandaloneSession(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.CreateSessionAsync(workId: null);
            FollowLatestAndScroll();
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

    private async void OnReasoningEffortSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_viewModel is not null
            && ReasoningEffortSelector.SelectedValue is string effort)
        {
            await _viewModel.SetReasoningEffortAsync(effort);
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

    private void OnShowSystemPrompt(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _systemPromptPreviousFocus = FocusManager.GetFocusedElement(XamlRoot) as Control;
        SystemPromptBox.Text = _viewModel.SystemInstructions;
        SystemPromptOverlay.Visibility = Visibility.Visible;
        ResizeSystemPromptModal(
            Math.Min(960, Math.Max(400, RootLayout.ActualWidth - 64)),
            Math.Min(680, Math.Max(300, RootLayout.ActualHeight - 64)));
        DispatcherQueue.TryEnqueue(() => SystemPromptBox.Focus(FocusState.Programmatic));
    }

    private void OnCopySystemPrompt(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(SystemPromptBox.Text);
        Clipboard.SetContent(package);
    }

    private void OnCloseSystemPrompt(object sender, RoutedEventArgs e) => CloseSystemPrompt();

    private void OnSystemPromptOverlayKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        CloseSystemPrompt();
        e.Handled = true;
    }

    private void CloseSystemPrompt()
    {
        SystemPromptOverlay.Visibility = Visibility.Collapsed;

        var previousFocus = _systemPromptPreviousFocus;
        _systemPromptPreviousFocus = null;
        if (previousFocus is not null)
        {
            DispatcherQueue.TryEnqueue(() => previousFocus.Focus(FocusState.Programmatic));
        }
    }

    private void OnSystemPromptResizeDragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSystemPromptModal(
            SystemPromptModal.ActualWidth + e.HorizontalChange,
            SystemPromptModal.ActualHeight + e.VerticalChange);

    private void OnSystemPromptOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SystemPromptOverlay.Visibility == Visibility.Visible)
        {
            ResizeSystemPromptModal(SystemPromptModal.ActualWidth, SystemPromptModal.ActualHeight);
        }
    }

    private void ResizeSystemPromptModal(double requestedWidth, double requestedHeight)
    {
        const double overlayMargin = 32;
        var maximumWidth = Math.Max(1, RootLayout.ActualWidth - overlayMargin);
        var maximumHeight = Math.Max(1, RootLayout.ActualHeight - overlayMargin);
        var minimumWidth = Math.Min(400, maximumWidth);
        var minimumHeight = Math.Min(300, maximumHeight);

        SystemPromptModal.Width = Math.Clamp(requestedWidth, minimumWidth, maximumWidth);
        SystemPromptModal.Height = Math.Clamp(requestedHeight, minimumHeight, maximumHeight);
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
