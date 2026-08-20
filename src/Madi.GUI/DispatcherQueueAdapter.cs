using Madi.GUI.Presentation;
using Microsoft.UI.Dispatching;

namespace Madi.GUI;

internal sealed class DispatcherQueueAdapter(DispatcherQueue queue) : IUiDispatcher
{
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            completion.SetException(new InvalidOperationException("UI 작업을 예약할 수 없습니다."));
        }
        return completion.Task;
    }
}
