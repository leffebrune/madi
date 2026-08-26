using System.Collections.ObjectModel;
using System.Text;
using Madi.App;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Madi.TUI;

public sealed class MadiWindow : Window
{
    private const int HeaderHeight = 3;
    private const int StatusHeight = 1;
    private const int MaximumInputHeight = 6;

    private readonly IApplication _app;
    private readonly MadiController _controller;
    private readonly UtilityInstaller _utilityInstaller;
    private readonly Label _header;
    private readonly Markdown _timeline;
    private readonly ListView _suggestions;
#pragma warning disable CS0618 // Terminal.Gui 2.4.17 still bundles TextView; it is required here to avoid another package/reflection surface for NativeAOT.
    private readonly TextView _input;
#pragma warning restore CS0618
    private readonly StatusBar _status;
    private readonly List<TimelineEntry> _entries = [];
    private readonly List<Suggestion> _currentSuggestions = [];
    private readonly List<string> _history = [];
    private CancellationTokenSource? _operationCancellation;
    private int _inputHeight = 1;
    private int _historyIndex;
    private int _selectedActivity = -1;
    private bool _followLatest = true;
    private bool _started;

    internal string DraftForTest => _input.Text;
    internal int InputHeightForTest => _inputHeight;
    internal bool SuggestionsVisibleForTest => _suggestions.Visible;
    internal string TimelineTextForTest => _timeline.Text;
    internal bool FollowLatestForTest => _followLatest;
    internal bool HasExpandedActivityForTest =>
        _entries.Any(entry => entry.ActivityId is not null && entry.IsExpanded);

    internal void FocusInputForTest() => _input.SetFocus();

    internal void ApplyEventForTest(AppEvent item) => ApplyEvent(item);

    internal (System.Drawing.Rectangle Timeline, System.Drawing.Rectangle Input,
        System.Drawing.Rectangle Status) GetFramesForTest() =>
        (_timeline.Frame, _input.Frame, _status.Frame);

    public MadiWindow(
        IApplication app,
        MadiController controller,
        UtilityInstaller utilityInstaller)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _utilityInstaller = utilityInstaller
            ?? throw new ArgumentNullException(nameof(utilityInstaller));

        Title = " Madi ";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        SetScheme(CreateScheme(ColorName16.White, ColorName16.Black));

        _header = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = HeaderHeight,
        };
        _header.SetScheme(CreateScheme(ColorName16.BrightCyan, ColorName16.Black));

        _timeline = new Markdown
        {
            X = 0,
            Y = HeaderHeight,
            Width = Dim.Fill(),
            Height = Dim.Fill(_inputHeight + StatusHeight),
            Text = "Madi를 준비하는 중입니다…",
            CanFocus = true,
            ShowHeadingPrefix = false,
            ShowCopyButtons = false,
        };
        _timeline.SetScheme(CreateScheme(ColorName16.White, ColorName16.Black));

        _suggestions = new ListView
        {
            X = 0,
            Y = Pos.AnchorEnd(_inputHeight + StatusHeight + 1),
            Width = Dim.Fill(),
            Height = 1,
            Visible = false,
            CanFocus = false,
        };
        _suggestions.SetScheme(CreateScheme(ColorName16.BrightYellow, ColorName16.Black));

#pragma warning disable CS0618
        _input = new TextView
        {
            X = 0,
            Y = Pos.AnchorEnd(_inputHeight + StatusHeight),
            Width = Dim.Fill(),
            Height = _inputHeight,
            Multiline = true,
            WordWrap = true,
            EnterKeyAddsLine = false,
            TabKeyAddsTab = false,
        };
#pragma warning restore CS0618
        _input.SetScheme(CreateScheme(ColorName16.White, ColorName16.Blue));

        _status = new StatusBar
        {
            X = 0,
            Y = Pos.AnchorEnd(StatusHeight),
            Width = Dim.Fill(),
            Height = StatusHeight,
            Text = " Enter 전송  Alt+Enter 줄바꿈  Ctrl+C 취소/종료  Ctrl+O 활동 상세 ",
        };
        _status.SetScheme(CreateScheme(ColorName16.Black, ColorName16.Gray));

        Add(_header, _timeline, _suggestions, _input, _status);
        _input.ContentsChanged += (_, _) => OnInputChanged();
        _input.KeyDown += (_, key) => HandleInputKey(key);
        Initialized += (_, _) => Start();
        UpdateHeader(_controller.State);
    }

    private static Scheme CreateScheme(ColorName16 foreground, ColorName16 background) =>
        new(new Attribute(foreground, background));

    private void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _controller.InitializeAsync();
                _app.Invoke(() =>
                {
                    UpdateHeader(_controller.State);
                    _input.SetFocus();
                });
                await foreach (var item in _utilityInstaller.EnsureAsync())
                {
                    _app.Invoke(() => ApplyEvent(item));
                }
            }
            catch (Exception exception)
            {
                _app.Invoke(() => AppendMessage(
                    $"초기화 오류: {exception.Message}",
                    TimelineRole.Error));
            }
        });
    }

    private void HandleInputKey(Key key)
    {
        if (key == Key.Enter.WithAlt)
        {
            _input.InsertText("\n");
            key.Handled = true;
            return;
        }

        if (key == Key.Enter)
        {
            SendInput();
            key.Handled = true;
            return;
        }

        if (key == Key.Tab && _suggestions.Visible)
        {
            CompleteSuggestion();
            key.Handled = true;
            return;
        }

        if (key == Key.Esc && _suggestions.Visible)
        {
            CloseSuggestions();
            key.Handled = true;
            return;
        }

        if (key == Key.CursorUp && _suggestions.Visible)
        {
            MoveSuggestion(-1);
            key.Handled = true;
            return;
        }

        if (key == Key.CursorDown && _suggestions.Visible)
        {
            MoveSuggestion(1);
            key.Handled = true;
            return;
        }

        if (key == Key.CursorUp)
        {
            MoveHistory(-1);
            key.Handled = true;
            return;
        }

        if (key == Key.CursorDown)
        {
            MoveHistory(1);
            key.Handled = true;
            return;
        }

        if (key == Key.PageUp)
        {
            _followLatest = false;
            _timeline.ScrollVertical(-Math.Max(1, _timeline.Viewport.Height - 1));
            key.Handled = true;
            return;
        }

        if (key == Key.PageDown)
        {
            _timeline.ScrollVertical(Math.Max(1, _timeline.Viewport.Height - 1));
            key.Handled = true;
            return;
        }

        if (key == Key.End)
        {
            _followLatest = true;
            FollowLatest();
            key.Handled = true;
            return;
        }

        if (key == Key.CursorUp.WithCtrl)
        {
            SelectActivity(-1);
            key.Handled = true;
            return;
        }

        if (key == Key.CursorDown.WithCtrl)
        {
            SelectActivity(1);
            key.Handled = true;
            return;
        }

        if (key == Key.O.WithCtrl)
        {
            ToggleSelectedActivity();
            key.Handled = true;
            return;
        }

        if (key == Key.C.WithCtrl)
        {
            HandleCancelOrExit();
            key.Handled = true;
        }
    }

    private void HandleCancelOrExit()
    {
        if (_operationCancellation is not null)
        {
            _operationCancellation.Cancel();
            return;
        }

        if (!string.IsNullOrEmpty(_input.Text))
        {
            _input.Text = string.Empty;
            return;
        }

        _app.RequestStop(this);
    }

    private void SendInput()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (_controller.State.IsBusy || _operationCancellation is not null)
        {
            AppendMessage(
                "다른 작업이 실행 중입니다. 입력 초안을 보존했습니다.",
                TimelineRole.Warning);
            return;
        }

        _history.Add(text);
        _historyIndex = _history.Count;
        _input.Text = string.Empty;
        CloseSuggestions();
        _operationCancellation = new CancellationTokenSource();
        var cancellation = _operationCancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in _controller.ExecuteAsync(text, cancellation.Token))
                {
                    _app.Invoke(() => ApplyEvent(item));
                }
            }
            catch (Exception exception)
            {
                _app.Invoke(() => AppendMessage(
                    $"오류: {exception.Message}",
                    TimelineRole.Error));
            }
            finally
            {
                _app.Invoke(() =>
                {
                    if (ReferenceEquals(_operationCancellation, cancellation))
                    {
                        _operationCancellation.Dispose();
                        _operationCancellation = null;
                    }
                    UpdateHeader(_controller.State);
                    _input.SetFocus();
                });
            }
        });
    }

    private void ApplyEvent(AppEvent item)
    {
        switch (item)
        {
            case AppStateChanged changed:
                UpdateHeader(changed.State);
                break;
            case AppUserInput user:
                _entries.Add(new TimelineEntry(TimelineRole.User, user.Text));
                break;
            case AppAssistantDelta delta:
                var assistant = _entries.LastOrDefault(entry =>
                    entry.Role == TimelineRole.Assistant && entry.ActivityId is null);
                if (assistant is null || _entries.LastOrDefault() != assistant)
                {
                    assistant = new TimelineEntry(TimelineRole.Assistant, string.Empty);
                    _entries.Add(assistant);
                }
                assistant.Summary += delta.Text;
                break;
            case AppMessage message:
                AppendMessage(message.Text, message.Kind switch
                {
                    AppMessageKind.Warning => TimelineRole.Warning,
                    AppMessageKind.Error => TimelineRole.Error,
                    _ => TimelineRole.Information,
                }, rebuild: false);
                break;
            case AppToolStarted tool:
                _entries.Add(new TimelineEntry(
                    TimelineRole.Tool,
                    $"⏳ {tool.Name}",
                    $"### 인자\n```json\n{tool.Arguments}\n```",
                    tool.CallId,
                    isRunning: true));
                break;
            case AppToolCompleted tool:
                CompleteActivity(
                    tool.CallId,
                    tool.IsError ? $"✗ {tool.Name}" : $"✓ {tool.Name}",
                    $"### 인자\n```json\n{tool.Arguments}\n```\n\n### 결과\n```text\n{tool.Result}\n```",
                    tool.IsError);
                break;
            case AppSubagentStarted subagent:
                _entries.Add(new TimelineEntry(
                    TimelineRole.Subagent,
                    $"⏳ 서브에이전트 {subagent.Name}",
                    $"### 프롬프트\n{subagent.Prompt}",
                    subagent.CallId,
                    isRunning: true));
                break;
            case AppSubagentCompleted subagent:
                CompleteActivity(
                    subagent.CallId,
                    subagent.IsError
                        ? $"✗ 서브에이전트 {subagent.Name}"
                        : $"✓ 서브에이전트 {subagent.Name}",
                    $"### 프롬프트\n{subagent.Prompt}\n\n### 결과\n{subagent.Response}",
                    subagent.IsError);
                break;
            case AppUsageReported usage:
                _entries.Add(new TimelineEntry(
                    TimelineRole.Usage,
                    $"토큰: 입력 {Format(usage.Usage.InputTokens)}, " +
                    $"출력 {Format(usage.Usage.OutputTokens)}, " +
                    $"합계 {Format(usage.Usage.TotalTokens)}"));
                break;
            case AppAuthenticationProgress authentication:
                AppendMessage(
                    authentication.Message,
                    authentication.Kind == AuthenticationProgressKind.Failed
                        ? TimelineRole.Error
                        : TimelineRole.Information,
                    rebuild: false);
                break;
            case AppExitRequested:
                _app.RequestStop(this);
                return;
        }

        RebuildTimeline();
    }

    private void CompleteActivity(
        string callId,
        string summary,
        string details,
        bool isError)
    {
        var entry = _entries.LastOrDefault(item => item.ActivityId == callId);
        if (entry is null)
        {
            return;
        }

        entry.Summary = summary;
        entry.Details = details;
        entry.IsRunning = false;
        entry.IsError = isError;
    }

    private void AppendMessage(
        string message,
        TimelineRole role,
        bool rebuild = true)
    {
        _entries.Add(new TimelineEntry(role, message));
        if (rebuild)
        {
            RebuildTimeline();
        }
    }

    private void RebuildTimeline()
    {
        var markdown = new StringBuilder();
        for (var index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            var selected = entry.ActivityId is not null && index == _selectedActivity ? "▶ " : string.Empty;
            switch (entry.Role)
            {
                case TimelineRole.User:
                    markdown.AppendLine("## 사용자").AppendLine().AppendLine(entry.Summary);
                    break;
                case TimelineRole.Assistant:
                    markdown.AppendLine("## 어시스턴트").AppendLine().AppendLine(entry.Summary);
                    break;
                case TimelineRole.Tool:
                    markdown.AppendLine($"> {selected}{entry.Summary}");
                    break;
                case TimelineRole.Subagent:
                    markdown.AppendLine($"> {selected}{entry.Summary}");
                    break;
                case TimelineRole.Error:
                    markdown.AppendLine($"> **오류:** {entry.Summary}");
                    break;
                case TimelineRole.Warning:
                    markdown.AppendLine($"> **주의:** {entry.Summary}");
                    break;
                case TimelineRole.Usage:
                    markdown.AppendLine($"*{entry.Summary}*");
                    break;
                default:
                    markdown.AppendLine(entry.Summary);
                    break;
            }

            if (entry.IsExpanded && !string.IsNullOrWhiteSpace(entry.Details))
            {
                markdown.AppendLine().AppendLine(entry.Details);
            }
            markdown.AppendLine();
        }

        _timeline.Text = markdown.Length == 0
            ? "이번 실행의 대화가 여기에 표시됩니다."
            : markdown.ToString();
        if (_followLatest)
        {
            FollowLatest();
        }
    }

    private void FollowLatest() => _timeline.ScrollVertical(int.MaxValue);

    private void SelectActivity(int direction)
    {
        var activities = _entries
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry.ActivityId is not null)
            .Select(item => item.index)
            .ToArray();
        if (activities.Length == 0)
        {
            return;
        }

        var current = Array.IndexOf(activities, _selectedActivity);
        current = current < 0
            ? (direction > 0 ? 0 : activities.Length - 1)
            : Math.Clamp(current + direction, 0, activities.Length - 1);
        _selectedActivity = activities[current];
        _followLatest = false;
        RebuildTimeline();
    }

    private void ToggleSelectedActivity()
    {
        if (_selectedActivity < 0 || _selectedActivity >= _entries.Count)
        {
            return;
        }

        var entry = _entries[_selectedActivity];
        if (entry.ActivityId is null)
        {
            return;
        }

        entry.IsExpanded = !entry.IsExpanded;
        RebuildTimeline();
    }

    private void OnInputChanged()
    {
        var lineCount = Math.Max(1, _input.Text.Count(character => character == '\n') + 1);
        var desiredHeight = Math.Clamp(lineCount, 1, MaximumInputHeight);
        if (desiredHeight != _inputHeight)
        {
            _inputHeight = desiredHeight;
            UpdateResponsiveLayout();
        }

        _ = RefreshSuggestionsAsync(_input.Text);
    }

    private async Task RefreshSuggestionsAsync(string input)
    {
        var suggestions = await SuggestionEngine.GetSuggestionsAsync(_controller, input);
        _app.Invoke(() =>
        {
            if (!string.Equals(input, _input.Text, StringComparison.Ordinal))
            {
                return;
            }

            _currentSuggestions.Clear();
            _currentSuggestions.AddRange(suggestions);
            if (_currentSuggestions.Count == 0)
            {
                CloseSuggestions();
                return;
            }

            _suggestions.SetSource(new ObservableCollection<string>(
                _currentSuggestions.Select(suggestion => suggestion.DisplayText)));
            _suggestions.SelectedItem = 0;
            _suggestions.Visible = true;
            UpdateResponsiveLayout();
        });
    }

    private void CompleteSuggestion()
    {
        if (!_suggestions.Visible
            || _suggestions.SelectedItem < 0
            || _suggestions.SelectedItem >= _currentSuggestions.Count)
        {
            return;
        }

        var selected = _suggestions.SelectedItem.GetValueOrDefault();
        _input.Text = _currentSuggestions[selected].CompletionText;
        _input.MoveEnd();
        CloseSuggestions();
    }

    private void MoveSuggestion(int direction)
    {
        if (_currentSuggestions.Count == 0)
        {
            return;
        }

        var selected = _suggestions.SelectedItem.GetValueOrDefault();
        _suggestions.SelectedItem = Math.Clamp(
            selected + direction,
            0,
            _currentSuggestions.Count - 1);
    }

    private void CloseSuggestions()
    {
        _suggestions.Visible = false;
        _currentSuggestions.Clear();
        UpdateResponsiveLayout();
    }

    private void MoveHistory(int direction)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        _input.Text = _historyIndex == _history.Count
            ? string.Empty
            : _history[_historyIndex];
        _input.MoveEnd();
    }

    private void UpdateResponsiveLayout()
    {
        var suggestionHeight = _suggestions.Visible
            ? Math.Min(8, _currentSuggestions.Count)
            : 0;
        _input.Y = Pos.AnchorEnd(_inputHeight + StatusHeight);
        _input.Height = _inputHeight;
        _suggestions.Y = Pos.AnchorEnd(_inputHeight + StatusHeight + suggestionHeight);
        _suggestions.Height = Math.Max(1, suggestionHeight);
        _timeline.Height = Dim.Fill(_inputHeight + StatusHeight + suggestionHeight);
        SetNeedsLayout();
    }

    private void UpdateHeader(AppState state)
    {
        var work = state.Work?.Title ?? "없음";
        var session = state.Session?.Title ?? "새 대화";
        var authentication = (state.AuthenticationKind, state.Authentication) switch
        {
            (AuthenticationKind.OAuth, AuthenticationState.Ready) => "로그인됨",
            (AuthenticationKind.OAuth, AuthenticationState.ActionRequired) => "로그인 필요",
            (AuthenticationKind.ApiKey, AuthenticationState.Ready) => "API 키 설정됨",
            (AuthenticationKind.ApiKey, AuthenticationState.ActionRequired) => "API 키 필요",
            (_, AuthenticationState.NotRequired) => "인증 불필요",
            _ => "확인 중",
        };
        var runState = state.RunState switch
        {
            AppRunState.RunningPrompt => "에이전트 실행 중",
            AppRunState.Authenticating => "인증 중",
            _ => "유휴",
        };
        _header.Text =
            $" 모델: {state.Model.QualifiedName}  |  Work: {work}  |  Session: {session}\n" +
            $" 경로: {state.WorkingDirectory}\n" +
            $" 인증: {authentication}  |  상태: {runState}";
    }

    private static string Format(int? value) => value?.ToString("N0") ?? "n/a";
}
