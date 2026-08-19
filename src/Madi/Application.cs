using Madi.Core;
using Madi.Runtime.Authentication;
using Madi.Services;
using Madi.UI;
using System.Runtime.InteropServices;

namespace Madi;

/// <summary>
/// Owns the application flow and delegates only presentation to the UI module.
/// </summary>
public sealed class Application
{
    private static readonly bool ShowRawProviderEvents = false;
    private readonly IAgent _agent;
    private readonly IWorkSessionStore _store;
    private readonly ModelOptions _defaultModel;
    private readonly IOAuthAuthentication _authentication;
    private readonly IUserInterface _ui;
    private readonly IGitHubReleaseFileDownloader _releaseFileDownloader;
    private readonly string _utilityBinDirectory;

    private Work? _currentWork;
    private Session? _currentSession;
    private readonly IReadOnlyList<AvailableModel> _availableModels;
    private ModelOptions _selectedModel;

    private enum CommandResult
    {
        NotCommand,
        Handled,
        Exit,
    }

    public Application(
        IAgent agent,
        IWorkSessionStore store,
        ModelOptions defaultModel,
        IReadOnlyList<AvailableModel> availableModels,
        IOAuthAuthentication authentication,
        IUserInterface ui,
        IGitHubReleaseFileDownloader releaseFileDownloader,
        string utilityBinDirectory)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _defaultModel = defaultModel ?? throw new ArgumentNullException(nameof(defaultModel));
        _selectedModel = _defaultModel;
        _availableModels = availableModels ?? throw new ArgumentNullException(nameof(availableModels));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _releaseFileDownloader = releaseFileDownloader
            ?? throw new ArgumentNullException(nameof(releaseFileDownloader));
        ArgumentException.ThrowIfNullOrWhiteSpace(utilityBinDirectory);
        _utilityBinDirectory = Path.GetFullPath(utilityBinDirectory);
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        await EnsureUtilitiesAsync(cancellationToken);

        var command = args.FirstOrDefault()?.ToLowerInvariant();
        switch (command)
        {
            case "login":
                await _authentication.LoginAsync(cancellationToken);
                _ui.WriteLine("로그인 정보를 저장했습니다.");
                return 0;

            case "logout":
                await _authentication.LogoutAsync(cancellationToken);
                _ui.WriteLine("로그인 정보를 삭제했습니다.");
                return 0;

            case "hello":
                await WriteResponseAsync("hello", cancellationToken);
                return 0;

            case null:
            case "chat":
            case "repl":
                return await RunReplAsync(cancellationToken);

            default:
                _ui.WriteLine("사용법: dotnet run --project src/Madi -- [repl | login | hello | logout]");
                return 1;
        }
    }

    private async Task EnsureUtilitiesAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            _ui.WriteLine("경고: fd/rg 자동 설치는 Windows x64에서만 지원됩니다.");
            return;
        }

        var utilities = new[]
        {
            new GitHubReleaseFileRequest(
                "sharkdp",
                "fd",
                "x86_64-pc-windows-msvc.zip",
                "fd.exe",
                Path.Combine(_utilityBinDirectory, "fd.exe")),
            new GitHubReleaseFileRequest(
                "BurntSushi",
                "ripgrep",
                "x86_64-pc-windows-msvc.zip",
                "rg.exe",
                Path.Combine(_utilityBinDirectory, "rg.exe")),
        };

        foreach (var utility in utilities)
        {
            if (File.Exists(utility.DestinationPath))
            {
                continue;
            }

            try
            {
                await _releaseFileDownloader.DownloadLatestReleaseFileAsync(
                    utility,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _ui.WriteLine(
                    $"경고: {utility.ArchiveEntryFileName}을(를) 설치하지 못했습니다: " +
                    exception.Message);
            }
        }
    }

    private async Task<int> RunReplAsync(CancellationToken cancellationToken)
    {
        if (_availableModels.Count == 0)
        {
            _ui.WriteLine("사용 가능한 모델이 없습니다.");
            return 1;
        }

        await TryRestoreCurrentConversationAsync(cancellationToken);
        _ui.WriteLine("Madi REPL입니다. 명령어: /model-list, /model 이름, /work 제목, /new, /exit");

        while (!cancellationToken.IsCancellationRequested)
        {
            _ui.Write($"[{_selectedModel.Model}] > ");
            var input = await _ui.ReadLineAsync(cancellationToken);
            if (input is null)
            {
                break;
            }

            var prompt = input.Trim();
            if (prompt.Length == 0)
            {
                continue;
            }

            try
            {
                var commandResult = await HandleCommandAsync(prompt, cancellationToken);
                if (commandResult == CommandResult.Exit)
                {
                    break;
                }

                if (commandResult == CommandResult.Handled)
                {
                    continue;
                }

                await WriteResponseAsync(prompt, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _ui.WriteLine($"오류: {exception.Message}");
            }
        }

        return 0;
    }

    private async Task<CommandResult> HandleCommandAsync(
        string input,
        CancellationToken cancellationToken)
    {
        if (!input.StartsWith('/'))
        {
            return CommandResult.NotCommand;
        }

        var separatorIndex = input.IndexOfAny([' ', '\t']);
        var command = separatorIndex < 0
            ? input
            : input[..separatorIndex];
        var argument = separatorIndex < 0
            ? string.Empty
            : input[(separatorIndex + 1)..].Trim();

        switch (command.ToLowerInvariant())
        {
            case "/exit" when argument.Length == 0:
                return CommandResult.Exit;

            case "/model-list" when argument.Length == 0:
                WriteModelList();
                return CommandResult.Handled;

            case "/model" when argument.Length > 0:
                await SelectModelAsync(argument, cancellationToken);
                return CommandResult.Handled;

            case "/model-list":
                _ui.WriteLine("사용법: /model-list");
                return CommandResult.Handled;

            case "/model":
                _ui.WriteLine("사용법: /model 모델명");
                return CommandResult.Handled;

            case "/work" when argument.Length > 0:
                await CreateWorkAsync(argument, cancellationToken);
                return CommandResult.Handled;

            case "/new" when argument.Length == 0:
                await CreateSessionAsync(cancellationToken);
                return CommandResult.Handled;

            case "/work":
                _ui.WriteLine("사용법: /work 제목");
                return CommandResult.Handled;

            case "/new":
                _ui.WriteLine("사용법: /new");
                return CommandResult.Handled;

            case "/exit":
                _ui.WriteLine("사용법: /exit");
                return CommandResult.Handled;

            default:
                _ui.WriteLine($"알 수 없는 명령어입니다: {command}");
                return CommandResult.Handled;
        }
    }

    private void WriteModelList()
    {
        foreach (var model in _availableModels)
        {
            var selected = string.Equals(
                    model.Name,
                    _selectedModel.Model,
                    StringComparison.OrdinalIgnoreCase)
                ? " *"
                : string.Empty;
            _ui.WriteLine(
                $"{model.Name} ({model.Provider}, context {model.MaxContextTokens:N0}){selected}");
        }
    }

    private async Task SelectModelAsync(string name, CancellationToken cancellationToken)
    {
        var model = _availableModels.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            _ui.WriteLine($"사용할 수 없는 모델입니다: {name}");
            return;
        }

        _selectedModel = new ModelOptions(model.Name, _selectedModel.ReasoningEffort);
        if (_currentSession is not null)
        {
            _currentSession.Model = _selectedModel;
            await _store.SaveSessionAsync(_currentSession, cancellationToken);
        }

        _ui.WriteLine($"모델을 선택했습니다: {model.Name}");
    }

    private async Task CreateWorkAsync(
        string title,
        CancellationToken cancellationToken)
    {
        await TryRestoreCurrentConversationAsync(cancellationToken);

        var work = new Work
        {
            Id = Guid.CreateVersion7().ToString(),
            Title = title,
            Context = string.Empty,
        };

        if (_currentSession is null)
        {
            _currentSession = CreateEmptySession(work.Id);
        }
        else
        {
            _currentSession.WorkId = work.Id;
        }

        work.Sessions.Add(_currentSession);
        _currentWork = work;

        await _store.SaveWorkAsync(work, cancellationToken);
        await _store.SaveSessionAsync(_currentSession, cancellationToken);
        await _store.SetCurrentAsync(_currentSession, cancellationToken);
        _ui.WriteLine($"Work를 만들었습니다: {work.Title}");
    }

    private async Task CreateSessionAsync(CancellationToken cancellationToken)
    {
        await TryRestoreCurrentConversationAsync(cancellationToken);

        var session = CreateEmptySession(_currentWork?.Id);
        if (_currentWork is not null)
        {
            _currentWork.Sessions.Add(session);
        }

        await _store.SaveSessionAsync(session, cancellationToken);
        await _store.SetCurrentAsync(session, cancellationToken);
        _currentSession = session;
        _ui.WriteLine("새 Session을 만들었습니다.");
    }

    private async Task WriteResponseAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        await EnsureCurrentConversationAsync(cancellationToken);
        var session = _currentSession!;

        var conversationStart = session.Conversation.Count;
        if (conversationStart == 0
            && (string.IsNullOrWhiteSpace(session.Title) || session.Title == "새 대화"))
        {
            session.Title = CreateSessionTitle(prompt);
        }

        var rawHeaderWritten = false;
        var assistantHeaderWritten = false;
        var assistantLineOpen = false;
        var responseCompleted = false;
        await foreach (var item in _agent.RespondAsync(
            _currentWork,
            session,
            prompt,
            cancellationToken))
        {
            switch (item)
            {
                case AgentRequestStarted:
                    // Request 본문은 콘솔에 원문 그대로 출력하지 않는다.
                    break;

                case AgentRawEventReceived raw when ShowRawProviderEvents:
                    if (!rawHeaderWritten)
                    {
                        _ui.WriteLine("=== RESPONSE SSE ===");
                        rawHeaderWritten = true;
                    }
                    _ui.Write(raw.Content);
                    break;

                case AgentTextDelta delta when !ShowRawProviderEvents:
                    if (!assistantHeaderWritten)
                    {
                        _ui.WriteLine("=== ASSISTANT ===");
                        assistantHeaderWritten = true;
                    }
                    _ui.Write(delta.Text);
                    assistantLineOpen = true;
                    break;

                case AgentToolCallCompleted toolCall:
                    FinishAssistantLine();
                    _ui.WriteLine($"=== TOOL CALL: {toolCall.Name} ({toolCall.CallId}) ===");
                    _ui.WriteLine(toolCall.Arguments);
                    break;

                case AgentToolExecutionCompleted result:
                    FinishAssistantLine();
                    var resultLabel = result.IsError ? "TOOL ERROR" : "TOOL COMPLETED";
                    _ui.WriteLine($"=== {resultLabel}: {result.Name} ({result.CallId}) ===");
                    break;

                case AgentSubagentStarted started:
                    FinishAssistantLine();
                    _ui.WriteLine($"=== SUBAGENT PROMPT: {started.Name} ({started.CallId}) ===");
                    _ui.WriteLine(started.Prompt);
                    break;

                case AgentSubagentCompleted completed:
                    FinishAssistantLine();
                    var subagentResult = completed.IsError ? "ERROR" : "RESPONSE";
                    _ui.WriteLine(
                        $"=== SUBAGENT {subagentResult}: {completed.Name} ({completed.CallId}) ===");
                    _ui.WriteLine(completed.Response);
                    break;

                case AgentUsageReported reported:
                    FinishAssistantLine();
                    var usage = reported.Usage;
                    _ui.WriteLine(
                        $"=== TOKEN USAGE: input={FormatTokenCount(usage.InputTokens)}, " +
                        $"cached={FormatTokenCount(usage.CachedTokens)}, " +
                        $"cache_write={FormatTokenCount(usage.CacheWriteTokens)}, " +
                        $"output={FormatTokenCount(usage.OutputTokens)}, " +
                        $"reasoning={FormatTokenCount(usage.ReasoningTokens)}, " +
                        $"total={FormatTokenCount(usage.TotalTokens)} ===");
                    break;

                case AgentResponseCompleted completed when ShowRawProviderEvents:
                    _ui.WriteLine("=== ASSISTANT ===");
                    _ui.WriteLine(completed.Text);
                    responseCompleted = true;
                    break;

                case AgentResponseCompleted when !ShowRawProviderEvents:
                    FinishAssistantLine();
                    responseCompleted = true;
                    break;
            }
        }

        void FinishAssistantLine()
        {
            if (!assistantLineOpen)
            {
                return;
            }

            _ui.WriteLine(string.Empty);
            assistantLineOpen = false;
        }

        if (responseCompleted)
        {
            await _store.AppendConversationAsync(
                session,
                conversationStart,
                cancellationToken);
            await _store.SaveSessionAsync(session, cancellationToken);
        }
    }

    private static string FormatTokenCount(int? count) => count?.ToString() ?? "n/a";

    private async Task EnsureCurrentConversationAsync(
        CancellationToken cancellationToken)
    {
        if (await TryRestoreCurrentConversationAsync(cancellationToken))
        {
            return;
        }

        _currentWork = null;
        _currentSession = CreateEmptySession(workId: null);
        await _store.SaveSessionAsync(_currentSession, cancellationToken);
        await _store.SetCurrentAsync(_currentSession, cancellationToken);
    }

    private async Task<bool> TryRestoreCurrentConversationAsync(
        CancellationToken cancellationToken)
    {
        if (_currentSession is not null)
        {
            return true;
        }

        var current = await _store.LoadCurrentAsync(cancellationToken);
        if (current is null)
        {
            return false;
        }

        _currentWork = current.Work;
        _currentSession = current.Session;
        _selectedModel = current.Session.Model;
        return true;
    }

    private Session CreateEmptySession(string? workId) => new()
    {
        Id = Guid.CreateVersion7().ToString(),
        Title = "새 대화",
        WorkId = workId,
        Model = _selectedModel,
    };

    private static string CreateSessionTitle(string prompt)
    {
        var normalized = string.Join(
            ' ',
            prompt.Split(
                [' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries));
        const int maximumLength = 40;

        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..maximumLength].TrimEnd()}…";
    }
}
