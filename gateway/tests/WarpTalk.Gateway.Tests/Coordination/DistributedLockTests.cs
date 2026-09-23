using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Gateway.Tests.Helpers;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.Gateway.Tests.Coordination;

/// <summary>
/// The lease rules, against a clock the test controls. The Redis store that enforces the same
/// compare-and-set semantics server-side is covered by <see cref="RedisCoordinationIntegrationTests"/>.
/// </summary>
public sealed class DistributedLockTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(10);

    private readonly ManualTimeProvider _time = new();
    private readonly InProcessLeaseStore _store;
    private readonly DistributedLockProvider _locks;

    public DistributedLockTests()
    {
        _store = new InProcessLeaseStore(_time);
        _locks = new DistributedLockProvider(_store, _time);
    }

    [Fact]
    public async Task Acquire_IsExclusive_UntilReleased()
    {
        var first = await _locks.TryAcquireAsync("job", Lease);
        var second = await _locks.TryAcquireAsync("job", Lease);

        Assert.NotNull(first);
        Assert.True(first!.IsHeld);
        Assert.Null(second);

        Assert.True(await first.ReleaseAsync());
        Assert.False(first.IsHeld);

        var third = await _locks.TryAcquireAsync("job", Lease);
        Assert.NotNull(third);
        Assert.True(third!.FencingToken > first.FencingToken);
    }

    [Fact]
    public async Task DifferentResources_DoNotContend()
    {
        Assert.NotNull(await _locks.TryAcquireAsync("a", Lease));
        Assert.NotNull(await _locks.TryAcquireAsync("b", Lease));
    }

    [Fact]
    public async Task Renew_ExtendsTheLease()
    {
        var lease = (await _locks.TryAcquireAsync("job", Lease))!;

        _time.Advance(TimeSpan.FromSeconds(7));
        Assert.True(await lease.TryRenewAsync());

        _time.Advance(TimeSpan.FromSeconds(7)); // 14s after acquire, 7s after renewal
        Assert.True(lease.IsHeld);
        Assert.Null(await _locks.TryAcquireAsync("job", Lease));
    }

    [Fact]
    public async Task Holder_StopsTrustingTheLease_BeforeTheStoreLetsAnyoneElseIn()
    {
        var lease = (await _locks.TryAcquireAsync("job", Lease))!;
        var margin = DistributedLockProvider.SafetyMarginFor(Lease);

        _time.Advance(Lease - margin);

        // Locally expired already...
        Assert.False(lease.IsHeld);
        // ...while the store still refuses a competitor: there is no instant with two holders.
        Assert.Null(await _locks.TryAcquireAsync("job", Lease));
    }

    [Fact]
    public async Task ExpiredLease_IsTakenOver_WithAHigherFence_AndTheOldHolderCannotRenewOrRelease()
    {
        var old = (await _locks.TryAcquireAsync("job", Lease))!;
        _time.Advance(Lease + TimeSpan.FromMilliseconds(1));

        var successor = await _locks.TryAcquireAsync("job", Lease);
        Assert.NotNull(successor);
        Assert.True(successor!.FencingToken > old.FencingToken);

        // Token-checked: the superseded holder can neither extend nor delete the new lease.
        Assert.False(await old.TryRenewAsync());
        Assert.False(old.IsHeld);
        Assert.False(await old.ReleaseAsync());
        Assert.True(successor.IsHeld);
        Assert.Null(await _locks.TryAcquireAsync("job", Lease));
    }

    [Fact]
    public async Task Release_WithAStaleToken_LeavesTheCurrentHolderAlone()
    {
        var current = (await _locks.TryAcquireAsync("job", Lease))!;

        Assert.False(await _store.ReleaseAsync("job", "not-the-token"));
        Assert.Null(await _locks.TryAcquireAsync("job", Lease));
        Assert.True(await current.ReleaseAsync());
    }

    [Fact]
    public async Task Renew_WhenTheStoreIsUnreachable_Throws_AndTheLeaseLapsesAtItsDeadline()
    {
        var store = new Mock<ILeaseStore>();
        store.Setup(s => s.TryAcquireAsync("job", It.IsAny<string>(), Lease)).ReturnsAsync(1L);
        store.Setup(s => s.TryRenewAsync("job", It.IsAny<string>(), Lease)).ThrowsAsync(new TimeoutException("redis down"));
        var locks = new DistributedLockProvider(store.Object, _time);

        var lease = (await locks.TryAcquireAsync("job", Lease))!;
        await Assert.ThrowsAsync<TimeoutException>(() => lease.TryRenewAsync());
        Assert.True(lease.IsHeld); // unknown is not lost — still inside the deadline

        _time.Advance(Lease);
        Assert.False(lease.IsHeld);
    }

    // ── TryRunExclusiveAsync ────────────────────────────────────

    [Fact]
    public async Task Exclusive_RunsWork_AndReleasesSoTheNextTickCanRun()
    {
        var runs = 0;
        var outcome = await _locks.TryRunExclusiveAsync(
            "tick", Lease, _ => { runs++; return Task.CompletedTask; }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(ExclusiveTickOutcome.Ran, outcome);
        Assert.Equal(1, runs);
        Assert.NotNull(await _locks.TryAcquireAsync("tick", Lease));
    }

    [Fact]
    public async Task Exclusive_SkipsWhileAnotherReplicaHoldsTheLease()
    {
        await using var other = await _locks.TryAcquireAsync("tick", Lease);
        var runs = 0;

        var outcome = await _locks.TryRunExclusiveAsync(
            "tick", Lease, _ => { runs++; return Task.CompletedTask; }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(ExclusiveTickOutcome.Skipped, outcome);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task Exclusive_HoldAfterCompletion_BlocksOtherReplicasUntilTheLeaseExpires()
    {
        await _locks.TryRunExclusiveAsync(
            "daily", Lease, _ => Task.CompletedTask, NullLogger.Instance, CancellationToken.None, holdAfterCompletion: true);

        var runs = 0;
        var again = await _locks.TryRunExclusiveAsync(
            "daily", Lease, _ => { runs++; return Task.CompletedTask; }, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(ExclusiveTickOutcome.Skipped, again);

        _time.Advance(Lease + TimeSpan.FromMilliseconds(1));
        var later = await _locks.TryRunExclusiveAsync(
            "daily", Lease, _ => { runs++; return Task.CompletedTask; }, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(ExclusiveTickOutcome.Ran, later);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Exclusive_WhenTheStoreIsUnreachable_SkipsWithoutThrowing()
    {
        var store = new Mock<ILeaseStore>();
        store.Setup(s => s.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));
        var locks = new DistributedLockProvider(store.Object, _time);
        var runs = 0;

        var outcome = await locks.TryRunExclusiveAsync(
            "tick", Lease, _ => { runs++; return Task.CompletedTask; }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(ExclusiveTickOutcome.LeaseUnavailable, outcome);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task Exclusive_WorkFailure_Propagates_AndReleasesTheLease()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _locks.TryRunExclusiveAsync(
            "tick", Lease, _ => throw new InvalidOperationException("boom"), NullLogger.Instance, CancellationToken.None));

        Assert.NotNull(await _locks.TryAcquireAsync("tick", Lease));
    }

    [Fact]
    public async Task Exclusive_LosingTheLeaseMidTick_CancelsTheWork()
    {
        // Real clock: the renewal loop runs on Task.Delay. A store that refuses the first renewal
        // stands in for "the lease expired and another replica took it".
        var store = new Mock<ILeaseStore>();
        store.Setup(s => s.TryAcquireAsync("tick", It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync(1L);
        store.Setup(s => s.TryRenewAsync("tick", It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync(false);
        var locks = new DistributedLockProvider(store.Object, TimeProvider.System);
        var cancelled = false;

        var outcome = await locks.TryRunExclusiveAsync(
            "tick",
            TimeSpan.FromMilliseconds(90),
            async ct =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(ExclusiveTickOutcome.LeaseUnavailable, outcome);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task Exclusive_LongTick_IsKeptAliveByRenewal()
    {
        var store = new InProcessLeaseStore(TimeProvider.System);
        var locks = new DistributedLockProvider(store, TimeProvider.System);
        var lease = TimeSpan.FromMilliseconds(150);

        var outcome = await locks.TryRunExclusiveAsync(
            "long",
            lease,
            async ct =>
            {
                // Four lease durations: without renewal a competitor would get in half way.
                for (var i = 0; i < 6; i++)
                {
                    await Task.Delay(100, ct);
                    Assert.Null(await locks.TryAcquireAsync("long", lease, ct));
                }
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(ExclusiveTickOutcome.Ran, outcome);
    }
}
