using Madi.Core;

namespace Madi.App;

public sealed class ConsoleApplication
{
    private readonly MadiController _controller;
    private readonly UtilityInstaller _utilityInstaller;
    private readonly IUserInterface _ui;

    public ConsoleApplication(
        MadiController controller,
        UtilityInstaller utilityInstaller,
        IUserInterface ui)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _utilityInstaller = utilityInstaller
            ?? throw new ArgumentNullException(nameof(utilityInstaller));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    public async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        await foreach (var item in _utilityInstaller.EnsureAsync(cancellationToken))
        {
            Render(item, new RenderState());
        }

        await _controller.InitializeAsync(cancellationToken);
        var command = args.FirstOrDefault()?.ToLowerInvariant();
        return command switch
        {
            "login" => await RunSingleAsync("/login", cancellationToken),
            "logout" => await RunSingleAsync("/logout", cancellationToken),
            null or "chat" or "repl" => await RunReplAsync(cancellationToken),
            _ => WriteUsageAndFail(),
        };
    }

    private async Task<int> RunReplAsync(CancellationToken cancellationToken)
    {
        if (_controller.AvailableModels.Count == 0)
        {
            _ui.WriteLine("사용 가능한 모델이 없습니다.");
            return 1;
        }

        _ui.WriteLine(
            "Madi REPL입니다. 명령어: /model-list, /model 이름, /work 제목, " +
            "/new, /login, /logout, /exit");
        while (!cancellationToken.IsCancellationRequested)
        {
            _ui.Write($"[{_controller.State.Model.Model}] > ");
            var input = await _ui.ReadLineAsync(cancellationToken);
            if (input is null)
            {
                break;
            }

            var renderState = new RenderState();
            var exitRequested = false;
            await foreach (var item in _controller.ExecuteAsync(input, cancellationToken))
            {
                Render(item, renderState);
                exitRequested |= item is AppExitRequested;
            }

            renderState.FinishAssistantLine(_ui);
            if (exitRequested)
            {
                break;
            }
        }

        return 0;
    }

    private async Task<int> RunSingleAsync(string input, CancellationToken cancellationToken)
    {
        var renderState = new RenderState();
        var failed = false;
        await foreach (var item in _controller.ExecuteAsync(input, cancellationToken))
        {
            Render(item, renderState);
            if (item is AppTurnCompleted { Succeeded: false })
            {
                failed = true;
            }
        }

        renderState.FinishAssistantLine(_ui);
        return failed ? 1 : 0;
    }

    private int WriteUsageAndFail()
    {
        _ui.WriteLine("사용법: dotnet run --project src/Madi -- [repl | chat | login | logout]");
        return 1;
    }

    private void Render(AppEvent item, RenderState state)
    {
        switch (item)
        {
            case AppMessage message:
                state.FinishAssistantLine(_ui);
                _ui.WriteLine(message.Text);
                break;
            case AppAssistantDelta delta:
                if (!state.AssistantHeaderWritten)
                {
                    _ui.WriteLine("=== ASSISTANT ===");
                    state.AssistantHeaderWritten = true;
                }
                _ui.Write(delta.Text);
                state.AssistantLineOpen = true;
                break;
            case AppToolStarted tool:
                state.FinishAssistantLine(_ui);
                _ui.WriteLine($"=== TOOL CALL: {tool.Name} ({tool.CallId}) ===");
                _ui.WriteLine(tool.Arguments);
                break;
            case AppToolCompleted tool:
                state.FinishAssistantLine(_ui);
                var resultLabel = tool.IsError ? "TOOL ERROR" : "TOOL COMPLETED";
                _ui.WriteLine($"=== {resultLabel}: {tool.Name} ({tool.CallId}) ===");
                break;
            case AppSubagentStarted subagent:
                state.FinishAssistantLine(_ui);
                _ui.WriteLine($"=== SUBAGENT PROMPT: {subagent.Name} ({subagent.CallId}) ===");
                _ui.WriteLine(subagent.Prompt);
                break;
            case AppSubagentCompleted subagent:
                state.FinishAssistantLine(_ui);
                var subagentResult = subagent.IsError ? "ERROR" : "RESPONSE";
                _ui.WriteLine(
                    $"=== SUBAGENT {subagentResult}: {subagent.Name} ({subagent.CallId}) ===");
                _ui.WriteLine(subagent.Response);
                break;
            case AppUsageReported reported:
                state.FinishAssistantLine(_ui);
                var usage = reported.Usage;
                _ui.WriteLine(
                    $"=== TOKEN USAGE: input={FormatTokenCount(usage.InputTokens)}, " +
                    $"cached={FormatTokenCount(usage.CachedTokens)}, " +
                    $"cache_write={FormatTokenCount(usage.CacheWriteTokens)}, " +
                    $"output={FormatTokenCount(usage.OutputTokens)}, " +
                    $"reasoning={FormatTokenCount(usage.ReasoningTokens)}, " +
                    $"total={FormatTokenCount(usage.TotalTokens)} ===");
                break;
            case AppAuthenticationProgress progress
                when progress.Kind is not AuthenticationProgressKind.Started:
                state.FinishAssistantLine(_ui);
                _ui.WriteLine(progress.Message);
                break;
        }
    }

    private static string FormatTokenCount(int? count) => count?.ToString() ?? "n/a";

    private sealed class RenderState
    {
        public bool AssistantHeaderWritten { get; set; }
        public bool AssistantLineOpen { get; set; }

        public void FinishAssistantLine(IUserInterface ui)
        {
            if (!AssistantLineOpen)
            {
                return;
            }

            ui.WriteLine(string.Empty);
            AssistantLineOpen = false;
        }
    }
}
