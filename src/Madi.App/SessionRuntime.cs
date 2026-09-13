using System.Text;

namespace Madi.App;

public sealed record SessionEvent(string SessionId, Guid RunId, long Sequence, AppEvent Event);

/// <summary>The workspace owns the task and journal; views only observe it.</summary>
public sealed class SessionRuntime
{
    private readonly object _gate = new();
    private readonly List<SessionEvent> _events = [];
    private CancellationTokenSource? _cancellation;
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
        lock (_gate) _events.Add(new SessionEvent(SessionId, RunId, _events.Count + 1L, item));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal Task Start(AppAction action, Guid runId, IDisposable lease, Action finished)
    {
        lock (_gate)
        {
            if (IsRunning) throw new InvalidOperationException("이 세션은 이미 실행 중입니다.");
            IsRunning = true;
            RunId = runId;
            _cancellation = new CancellationTokenSource();
            // A start gate ensures Completion exists before any observer receives events.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Completion = PumpAsync(start.Task, action, _cancellation, lease, finished);
            start.SetResult();
            return Completion;
        }
    }

    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    private async Task PumpAsync(Task start, AppAction action, CancellationTokenSource cancellation,
        IDisposable lease, Action finished)
    {
        await start.ConfigureAwait(false);
        var cleaned = false;
        try
        {
            var buffer = new StringBuilder();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
            await using var enumerator = Controller.ExecuteAsync(action, cancellation.Token).GetAsyncEnumerator();
            var next = enumerator.MoveNextAsync().AsTask();
            var tick = timer.WaitForNextTickAsync().AsTask();
            while (true)
            {
                // Keep one pending MoveNext while a timer can flush a lone delta. All journal
                // writes still happen in this one consumer, preserving event order.
                if (await Task.WhenAny(next, tick).ConfigureAwait(false) == tick)
                {
                    await tick.ConfigureAwait(false);
                    if (buffer.Length > 0)
                    {
                        Publish(new AppAssistantDelta(buffer.ToString()));
                        buffer.Clear();
                    }
                    tick = timer.WaitForNextTickAsync().AsTask();
                    continue;
                }
                if (!await next.ConfigureAwait(false)) break;
                var item = enumerator.Current;
                if (item is AppAssistantDelta delta)
                {
                    buffer.Append(delta.Text);
                }
                else
                {
                    if (buffer.Length > 0)
                    {
                        Publish(new AppAssistantDelta(buffer.ToString()));
                        buffer.Clear();
                    }
                    Publish(item);
                }
                next = enumerator.MoveNextAsync().AsTask();
            }
            if (buffer.Length > 0) Publish(new AppAssistantDelta(buffer.ToString()));
            cleaned = true;
        }
        catch (Exception exception)
        {
            cleaned = false;
            CleanupFailure = exception;
            Publish(new AppMessage($"실행 정리 실패: {exception.Message}", AppMessageKind.Error));
            Publish(new AppTurnCompleted(false, cancellation.IsCancellationRequested));
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _cancellation = null;
                cancellation.Dispose();
                IsRunning = !cleaned;
            }
            // A failed enumerator/tool cleanup is not a successful release. Close observes
            // the faulted Completion and retains this tab and claim for an explicit error.
            if (cleaned) lease.Dispose();
            finished();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
