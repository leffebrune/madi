namespace Madi.App;

public readonly record struct TabId(Guid Value)
{
    public static TabId New() => new(Guid.NewGuid());
}

public sealed class SessionInUseException(string sessionId, TabId owner)
    : InvalidOperationException("다른 탭에서 사용 중인 세션입니다.")
{
    public string SessionId { get; } = sessionId;
    public TabId Owner { get; } = owner;
}

/// <summary>All operations are atomic; leases never span this monitor across I/O.</summary>
public sealed class SessionOwnershipRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<TabId, TabState> _tabs = [];
    private readonly Dictionary<string, Claim> _claims = new(StringComparer.Ordinal);

    public void Register(TabId tab)
    {
        lock (_gate) _tabs.Add(tab, new TabState());
    }

    public string? Selected(TabId tab)
    {
        lock (_gate) return _tabs[tab].Selected;
    }

    public TabId? Owner(string session)
    {
        lock (_gate) return _claims.TryGetValue(session, out var claim) ? claim.Owner : null;
    }

    public int ClaimCount { get { lock (_gate) return _claims.Count; } }

    public SelectionReservation Reserve(TabId tab, string session)
    {
        lock (_gate)
        {
            var state = RequireOpen(tab);
            var claim = GetOrClaim(tab, session);
            // A rejected request does not invalidate an earlier valid request.
            var generation = ++state.Generation;
            var token = Guid.NewGuid();
            claim.Reservations.Add(token);
            return new SelectionReservation(this, tab, session, generation, token);
        }
    }

    public bool Commit(SelectionReservation reservation)
    {
        lock (_gate)
        {
            if (!_tabs.TryGetValue(reservation.Tab, out var tab) || tab.Closing
                || tab.Generation != reservation.Generation
                || !_claims.TryGetValue(reservation.Session, out var claim)
                || claim.Owner != reservation.Tab || !claim.Reservations.Remove(reservation.Token))
                return false;
            var previous = tab.Selected;
            tab.Selected = reservation.Session;
            claim.Selected = true;
            if (previous is not null && previous != reservation.Session
                && _claims.TryGetValue(previous, out var old))
            {
                old.Selected = false;
                Prune(previous, old);
            }
            return true;
        }
    }

    public IDisposable BeginRun(TabId tab, string session, Guid runId)
    {
        lock (_gate)
        {
            RequireOpen(tab);
            var claim = GetOrClaim(tab, session);
            if (claim.Run is not null)
                throw new InvalidOperationException("이 세션은 이미 실행 또는 저장 중입니다.");
            claim.Run = runId;
            return new ActionLease(() =>
            {
                lock (_gate)
                {
                    if (_claims.TryGetValue(session, out var current)
                        && current.Owner == tab && current.Run == runId)
                    {
                        current.Run = null;
                        Prune(session, current);
                    }
                }
            });
        }
    }

    public IDisposable BeginMutation(TabId tab, IEnumerable<string> sessions)
    {
        lock (_gate)
        {
            RequireOpen(tab);
            var ids = sessions.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var id in ids)
            {
                if (!_claims.TryGetValue(id, out var claim)) continue;
                if (claim.Owner != tab) throw new SessionInUseException(id, claim.Owner);
                if (claim.Run is not null || claim.Reservations.Count > 0 || claim.Mutation is not null)
                    throw new InvalidOperationException("실행, 저장 또는 선택 중인 세션은 변경할 수 없습니다.");
            }
            var token = Guid.NewGuid();
            foreach (var id in ids) GetOrClaim(tab, id).Mutation = token;
            return new ActionLease(() =>
            {
                lock (_gate)
                {
                    foreach (var id in ids)
                        if (_claims.TryGetValue(id, out var claim) && claim.Mutation == token)
                        {
                            claim.Mutation = null;
                            Prune(id, claim);
                        }
                }
            });
        }
    }

    public void BeginClose(TabId tab)
    {
        lock (_gate)
        {
            _tabs[tab].Closing = true;
            _tabs[tab].Generation++;
        }
    }

    public void CompleteClose(TabId tab)
    {
        lock (_gate)
        {
            if (_claims.Values.Any(c => c.Owner == tab && (c.Run is not null || c.Mutation is not null
                || c.Reservations.Count > 0)))
                throw new InvalidOperationException("탭의 실행 또는 정리가 아직 끝나지 않았습니다.");
            foreach (var id in _claims.Where(p => p.Value.Owner == tab).Select(p => p.Key).ToArray())
                _claims.Remove(id);
            _tabs.Remove(tab);
        }
    }

    private TabState RequireOpen(TabId tab)
    {
        if (!_tabs.TryGetValue(tab, out var state) || state.Closing)
            throw new InvalidOperationException("닫는 중인 탭입니다.");
        return state;
    }

    private Claim GetOrClaim(TabId tab, string session)
    {
        if (_claims.TryGetValue(session, out var claim))
        {
            if (claim.Owner != tab) throw new SessionInUseException(session, claim.Owner);
            if (claim.Mutation is not null) throw new InvalidOperationException("세션을 변경하는 중입니다.");
            return claim;
        }
        claim = new Claim(tab);
        _claims.Add(session, claim);
        return claim;
    }

    private void Prune(string session, Claim claim)
    {
        if (!claim.Selected && claim.Run is null && claim.Mutation is null && claim.Reservations.Count == 0)
            _claims.Remove(session);
    }

    private void Release(SelectionReservation reservation)
    {
        lock (_gate)
            if (_claims.TryGetValue(reservation.Session, out var claim) && claim.Owner == reservation.Tab)
            {
                claim.Reservations.Remove(reservation.Token);
                Prune(reservation.Session, claim);
            }
    }

    public sealed class SelectionReservation : IDisposable
    {
        private readonly SessionOwnershipRegistry _registry;
        internal SelectionReservation(SessionOwnershipRegistry registry, TabId tab, string session,
            long generation, Guid token)
        {
            _registry = registry;
            Tab = tab;
            Session = session;
            Generation = generation;
            Token = token;
        }
        public TabId Tab { get; }
        public string Session { get; }
        internal long Generation { get; }
        internal Guid Token { get; }
        public void Dispose() => _registry.Release(this);
    }

    private sealed class TabState
    {
        public string? Selected;
        public long Generation;
        public bool Closing;
    }

    private sealed class Claim(TabId owner)
    {
        public TabId Owner { get; } = owner;
        public bool Selected;
        public Guid? Run;
        public Guid? Mutation;
        public HashSet<Guid> Reservations { get; } = [];
    }

    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
