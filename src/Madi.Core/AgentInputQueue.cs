namespace Madi.Core;

public sealed record AgentInput(Guid Id, string Text);

/// <summary>One execution's mailbox. Closing the model loop does not discard late input;
/// the runtime can take it for a subsequent turn after durable completion.</summary>
public sealed class AgentInputQueue
{
    private readonly object _gate = new();
    private readonly Queue<AgentInput> _pending = new();
    private readonly HashSet<Guid> _accepted = [];
    private bool _closed;

    public bool Enqueue(AgentInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Text);
        lock (_gate)
        {
            if (!_accepted.Add(input.Id)) return false;
            _pending.Enqueue(input);
            return true;
        }
    }

    public IReadOnlyList<AgentInput> Drain()
    {
        lock (_gate)
        {
            if (_closed) return [];
            var result = _pending.ToArray();
            _pending.Clear();
            return result;
        }
    }

    public bool TryClose()
    {
        lock (_gate)
        {
            if (_pending.Count != 0) return false;
            _closed = true;
            return true;
        }
    }

    public AgentInput? BeginNextTurn()
    {
        lock (_gate)
        {
            _closed = false;
            return _pending.Count == 0 ? null : _pending.Dequeue();
        }
    }

    public IReadOnlyList<AgentInput> Abandon()
    {
        lock (_gate)
        {
            _closed = true;
            var result = _pending.ToArray();
            _pending.Clear();
            return result;
        }
    }
}
