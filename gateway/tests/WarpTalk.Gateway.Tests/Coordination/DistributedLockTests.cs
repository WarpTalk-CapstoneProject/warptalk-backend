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

    /// <summary>
    /// The renewal loop is paced by <see cref="_time"/> as well, so "a renewal was refused" is an
    /// event the test causes rather than one it waits for. A store that refuses every renewal
    /// stands in for "the lease expired and another replica took it".
    /// </summary>
    [Fact]
    public async Task Exclusive_LosingTheLeaseMidTick_CancelsTheWork()
    {
        var store = new Mock<ILeaseStore>();
        store.Setup(s => s.TryAcquireAsync("tick", It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync(1L);
        store.Setup(s => s.TryRenewAsync("tick", It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync(false);
        var locks = new DistributedLockProvider(store.Object, _time);
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;

        var tick = locks.TryRunExclusiveAsync(
            "tick",
            Lease,
            async ct =>
            {
                working.SetResult();
                try
                {
                    // Never completes on its own: the tick ends only because the lease was lost.
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            },
            NullLogger.Instance,
            CancellationToken.None,
            time: _time);

        await working.Task.WaitAsync(TestTimeout);
        await AdvanceToNextRenewalAsync();

        // WaitAsync, not a bare await: an implementation that stopped cancelling the work would
        // otherwise hang this test for the whole run instead of failing it.
        Assert.Equal(ExclusiveTickOutcome.LeaseUnavailable, await tick.WaitAsync(TestTimeout));
        Assert.True(cancelled);
    }

    /// <summary>
    /// A tick that runs for longer than its own lease keeps it, because the renewal loop extends
    /// it underneath — and no competitor gets in at any point along the way.
    ///
    /// Every clock this touches is <see cref="_time"/>: lease expiry in the store, the holder's
    /// local deadline, and the renewal loop's delay. The test moves time itself, and each step
    /// waits for the renewal it just caused to come back from the store before asking whether a
    /// competitor can acquire — so there is no window in which the answer depends on how quickly
    /// the machine got round to running the renewal.
    ///
    /// It used to run on the real clock: a 150ms lease, six 100ms sleeps, and an assertion that a
    /// competitor was still locked out. On a loaded CI runner one late wake-up let the lease lapse
    /// between renewals and the test failed — on PRs that had nothing to do with the gateway.
    /// </summary>
    [Fact]
    public async Task Exclusive_LongTick_IsKeptAliveByRenewal()
    {
        var renewals = new RenewalSignal(_store);
        var locks = new DistributedLockProvider(renewals, _time);
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tick = locks.TryRunExclusiveAsync(
            "long",
            Lease,
            async _ =>
            {
                working.SetResult();
                await finishWork.Task;
            },
            NullLogger.Instance,
            CancellationToken.None,
            time: _time);

        await working.Task.WaitAsync(TestTimeout);

        // Six renewal intervals — four lease durations in total. Without renewal the lease would
        // have lapsed a third of the way in and a competitor would be waiting on the second pass.
        for (var i = 0; i < 6; i++)
        {
            var renewed = renewals.Next();
            await AdvanceToNextRenewalAsync();
            Assert.True(await renewed.WaitAsync(TestTimeout), $"renewal {i + 1} was refused");
            Assert.Null(await _locks.TryAcquireAsync("long", Lease));
        }

        finishWork.SetResult();
        Assert.Equal(ExclusiveTickOutcome.Ran, await tick.WaitAsync(TestTimeout));
    }

    /// <summary>
    /// Waits until the renewal loop has armed its next delay, then moves the clock one renewal
    /// interval — the interval <see cref="ExclusiveTickExtensions"/> derives from the lease — plus
    /// a tick, so the delay is strictly due rather than exactly due.
    ///
    /// The wait is what keeps this honest: advancing before the delay exists would move the clock
    /// past a deadline nothing was waiting on yet.
    /// </summary>
    private async Task AdvanceToNextRenewalAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        await _time.WaitForArmedTimerAsync(timeout.Token);
        _time.Advance(TimeSpan.FromTicks(Lease.Ticks / 3) + TimeSpan.FromTicks(1));
    }

    /// <summary>
    /// How long a step may take before the test gives up — a deadlock guard, not a timing
    /// assumption. Nothing here is supposed to take any wall-clock time at all.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A store that hands the test a task completing when the NEXT renewal has been answered.
    ///
    /// The sync point the whole thing rests on: advancing the clock only makes the renewal due,
    /// and the loop that performs it runs on another thread. Asserting straight after the advance
    /// would be asking the question before the renewal had happened — the same race, moved.
    /// </summary>
    private sealed class RenewalSignal(ILeaseStore inner) : ILeaseStore
    {
        private TaskCompletionSource<bool>? _next;

        public Task<bool> Next()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _next, signal);
            return signal.Task;
        }

        public Task<long?> TryAcquireAsync(string resource, string token, TimeSpan leaseDuration)
            => inner.TryAcquireAsync(resource, token, leaseDuration);

        public async Task<bool> TryRenewAsync(string resource, string token, TimeSpan leaseDuration)
        {
            var renewed = await inner.TryRenewAsync(resource, token, leaseDuration);
            Interlocked.Exchange(ref _next, null)?.SetResult(renewed);
            return renewed;
        }

        public Task<bool> ReleaseAsync(string resource, string token) => inner.ReleaseAsync(resource, token);
    }
}
