using System.Text;
using Madi.Core;

namespace Madi.App;

public sealed record SessionEvent(string SessionId, Guid RunId, long Sequence, AppEvent Event);

/// <summary>The workspace owns the task, input mailbox and journal; views only observe it.</summary>
public sealed class SessionRuntime
{
    private readonly object _gate = new();
    private readonly List<SessionEvent> _events = [];
    private CancellationTokenSource? _cancellation;
    private AgentInputQueue? _inputs;
    private bool _accepting;
    private bool _failed;
    private readonly Dictionary<Guid, AgentInput> _undelivered = [];
    private readonly HashSet<Guid> _accepted = [];

    internal bool HasAccepted(Guid messageId)
    {
        lock (_gate) return _accepted.Contains(messageId);
    }
    internal bool CompletedSuccessfully { get; private set; }
    internal SessionRuntime(MadiController controller)
    {
        Controller = controller;
        SessionId = controller.State.Session!.Id;
        InitialConversation = controller.GetCurrentConversation()!;
    }

    public MadiController Controller { get; }
    public string SessionId { get; }
    public ConversationSnapshot InitialConversation { get; }
    public Guid RunId { get; private set; }
    public bool IsRunning { get; private set; }
    public bool CanSubmitPrompt
    {
        get
        {
            lock (_gate) return !IsRunning || (_inputs is not null && !_failed
                && _cancellation?.IsCancellationRequested == false && CleanupFailure is null);
        }
    }
    public Exception? CleanupFailure { get; private set; }
    public bool HasUnseenResult { get; internal set; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public event EventHandler? Changed;

    public IReadOnlyList<SessionEvent> EventsAfter(long sequence)
    {
        lock (_gate) return _events.GetRange((int)Math.Clamp(sequence, 0, _events.Count),
            _events.Count - (int)Math.Clamp(sequence, 0, _events.Count)).ToArray();
    }

    internal void Publish(AppEvent item)
    {
        lock (_gate)
        {
            if (item is AppInputStatus { State: InputDeliveryState.Included } included)
                _undelivered.Remove(included.Input.Id);
            if (item is AppTurnCompleted { Succeeded: false }) _failed = true;
            _events.Add(new SessionEvent(SessionId, RunId, _events.Count + 1L, item));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // False means normal finalization has passed its acceptance boundary. The coordinator
    // waits for Completion and retries in the same session, never starting a second agent.
    internal bool TrySubmit(AgentInput input, Guid observedRunId)
    {
        lock (_gate)
        {
            if (_cancellation?.IsCancellationRequested == true || CleanupFailure is not null || _failed)
                throw new InvalidOperationException("중지 또는 오류 정리 중에는 입력을 전달할 수 없습니다.");
            if (IsRunning && _inputs is null)
                throw new InvalidOperationException("현재 작업에는 추가 지시를 전달할 수 없습니다.");
            if (!_accepting || observedRunId != RunId) return false;
            if (!_accepted.Add(input.Id)) return true;
            _inputs!.Enqueue(input);
            _undelivered.Add(input.Id, input);
            // Delivery publication must acquire this same gate, so Pending always comes first.
            _events.Add(new SessionEvent(SessionId, RunId, _events.Count + 1L,
                new AppInputStatus(input, InputDeliveryState.Pending)));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal Task Start(AppAction action, Guid runId, IDisposable lease, Action finished,
        Guid? initialMessageId = null)
    {
        lock (_gate)
        {
            if (IsRunning) throw new InvalidOperationException("이 세션은 이미 실행 중입니다.");
            IsRunning = true;
            _failed = false;
            CompletedSuccessfully = false;
            RunId = runId;
            if (initialMessageId is { } id) _accepted.Add(id);
            _inputs = action is SubmitPromptAction prompt && !prompt.Text.TrimStart().StartsWith('/')
                ? new AgentInputQueue() : null;
            _accepting = _inputs is not null;
            Controller.Inputs = _inputs;
            _cancellation = new CancellationTokenSource();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Completion = PumpAsync(start.Task, action, _cancellation, lease, finished);
            start.SetResult();
            return Completion;
        }
    }

    public void Cancel()
    {
        lock (_gate) { _accepting = false; _cancellation?.Cancel(); }
    }

    private async Task PumpAsync(Task start, AppAction action, CancellationTokenSource cancellation,
        IDisposable lease, Action finished)
    {
        await start.ConfigureAwait(false);
        var cleaned = false;
        try
        {
            while (true)
            {
                var succeeded = await PumpTurnAsync(action, cancellation.Token).ConfigureAwait(false);
                AgentInput? next;
                lock (_gate)
                {
                    CompletedSuccessfully = succeeded && !cancellation.IsCancellationRequested;
                    next = CompletedSuccessfully
                        ? _inputs?.BeginNextTurn() : null;
                    if (next is null) _accepting = false;
                }
                if (next is null) break;
                // Retain the ownership lease and Completion across continuation turns.
                // Each turn is committed before the next begins.
                Controller.ResumedInput = next;
                action = new SubmitPromptAction(next.Text);
            }
            cleaned = true;
        }
        catch (Exception exception)
        {
            CleanupFailure = exception;
            Publish(new AppMessage($"실행 정리 실패: {exception.Message}", AppMessageKind.Error));
            Publish(new AppTurnCompleted(false, cancellation.IsCancellationRequested));
            throw;
        }
        finally
        {
            AgentInput[] undelivered;
            lock (_gate)
            {
                _accepting = false;
                _inputs?.Abandon();
                undelivered = _undelivered.Values.ToArray();
                _undelivered.Clear();
            }
            foreach (var input in undelivered)
                Publish(new AppInputStatus(input, InputDeliveryState.Interrupted));
            Controller.Inputs = null;
            Controller.ResumedInput = null;
            lock (_gate)
            {
                _cancellation = null;
                cancellation.Dispose();
                // Release before advertising idle so a new run cannot race the old lease.
                if (cleaned) lease.Dispose();
                IsRunning = !cleaned;
            }
            finished();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> PumpTurnAsync(AppAction action, CancellationToken cancellation)
    {
        var succeeded = false;
        var buffer = new StringBuilder();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
        await using var enumerator = Controller.ExecuteAsync(action, cancellation).GetAsyncEnumerator();
        var next = enumerator.MoveNextAsync().AsTask();
        var tick = timer.WaitForNextTickAsync().AsTask();
        while (true)
        {
            if (await Task.WhenAny(next, tick).ConfigureAwait(false) == tick)
            {
                await tick.ConfigureAwait(false);
                Flush();
                tick = timer.WaitForNextTickAsync().AsTask();
                continue;
            }
            if (!await next.ConfigureAwait(false)) break;
            var item = enumerator.Current;
            if (item is AppAssistantDelta delta) buffer.Append(delta.Text);
            else
            {
                Flush();
                if (item is AppTurnCompleted completed) succeeded = completed.Succeeded;
                Publish(item);
            }
            next = enumerator.MoveNextAsync().AsTask();
        }
        Flush();
        return succeeded;

        void Flush()
        {
            if (buffer.Length == 0) return;
            Publish(new AppAssistantDelta(buffer.ToString()));
            buffer.Clear();
        }
    }
}
