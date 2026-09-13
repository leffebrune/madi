using Madi.App;

namespace Madi.App.Tests;

public sealed class SessionOwnershipRegistryTests
{
    private readonly SessionOwnershipRegistry _registry = new();
    private TabId Tab() { var tab = TabId.New(); _registry.Register(tab); return tab; }
    private void Select(TabId tab, string session)
    {
        using var lease = _registry.Reserve(tab, session);
        Assert.True(_registry.Commit(lease));
    }

    [Fact]
    public async Task A11_ConcurrentReservationsHaveExactlyOneOwner()
    {
        var tabs = new[] { Tab(), Tab() };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = tabs.Select(tab => Task.Run(async () =>
        {
            await start.Task;
            try { Select(tab, "A"); return true; }
            catch (SessionInUseException) { return false; }
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(results, x => x);
        Assert.Null(_registry.Selected(tabs[Array.IndexOf(results, false)]));
    }

    [Fact]
    public void A12_FailureAndLateNavigationPreserveSelectionWithoutLeaking()
    {
        var tab = Tab(); Select(tab, "A");
        using (var failed = _registry.Reserve(tab, "B")) { }
        Assert.Equal("A", _registry.Selected(tab));
        using (var slow = _registry.Reserve(tab, "B"))
        {
            Select(tab, "C");
            Assert.False(_registry.Commit(slow));
        }
        Assert.Equal("C", _registry.Selected(tab));
        Assert.Null(_registry.Owner("A"));
        Assert.Null(_registry.Owner("B"));
        Assert.Equal(1, _registry.ClaimCount);
    }

    [Fact]
    public void A08_A09_A10_RunRetainsOwnershipUntilCleanupThenReleases()
    {
        var one = Tab(); var two = Tab(); Select(one, "A");
        var run = _registry.BeginRun(one, "A", Guid.NewGuid());
        Select(one, "C");
        Assert.Throws<SessionInUseException>(() => _registry.Reserve(two, "A"));
        Select(one, "A"); Select(one, "C");
        run.Dispose();
        Select(two, "A");
        Assert.Equal(two, _registry.Owner("A"));
    }

    [Fact]
    public void A13_StaleReleaseCannotRemoveNewOwnerOrNewRun()
    {
        var one = Tab(); var two = Tab();
        var old = _registry.Reserve(one, "A"); old.Dispose();
        Select(two, "A"); old.Dispose();
        var run = _registry.BeginRun(two, "A", Guid.NewGuid()); run.Dispose();
        using var newer = _registry.BeginRun(two, "A", Guid.NewGuid());
        run.Dispose();
        Assert.Throws<InvalidOperationException>(() => _registry.BeginRun(two, "A", Guid.NewGuid()));
        Assert.Equal(two, _registry.Owner("A"));
    }

    [Fact]
    public void A16_MutationClaimsAllChildrenOrNoneAndBlocksSelectionAndRun()
    {
        var one = Tab(); var two = Tab(); Select(two, "B");
        Assert.Throws<SessionInUseException>(() => _registry.BeginMutation(one, ["A", "B"]));
        Assert.Null(_registry.Owner("A"));
        using (_registry.BeginMutation(one, ["A"]))
        {
            Assert.Throws<SessionInUseException>(() => _registry.Reserve(two, "A"));
            Assert.Throws<InvalidOperationException>(() => _registry.BeginRun(one, "A", Guid.NewGuid()));
        }
        Assert.Null(_registry.Owner("A"));
    }

    [Fact]
    public void A17_A22_CloseRejectsNewWorkAndWaitsForReservationsAndRuns()
    {
        var tab = Tab(); Select(tab, "A");
        var run = _registry.BeginRun(tab, "A", Guid.NewGuid());
        var pending = _registry.Reserve(tab, "B");
        _registry.BeginClose(tab);
        Assert.False(_registry.Commit(pending));
        Assert.Throws<InvalidOperationException>(() => _registry.CompleteClose(tab));
        Assert.Throws<InvalidOperationException>(() => _registry.Reserve(tab, "C"));
        run.Dispose(); pending.Dispose();
        _registry.CompleteClose(tab);
        Assert.Equal(0, _registry.ClaimCount);
    }
}
