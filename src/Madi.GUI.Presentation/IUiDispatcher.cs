namespace Madi.GUI.Presentation;

public interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}

public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }
}
