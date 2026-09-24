namespace WarpTalk.Gateway.Tests.Helpers;

/// <summary>
/// A clock that only moves when the test says so. Covers wall time, timestamps AND timers, so
/// anything built on <c>Task.Delay(…, TimeProvider, …)</c> is driven by <see cref="Advance"/>
/// rather than by how busy the machine running the test happens to be.
///
/// WHY THE TIMERS ARE HERE. Without them <see cref="TimeProvider.CreateTimer"/> falls back to the
/// base implementation, which is the system timer — so a test could control lease expiry while the
/// renewal loop it was testing still ran on wall-clock delays. That mixture is what made
/// <c>DistributedLockTests.Exclusive_LongTick_IsKeptAliveByRenewal</c> fail at random on CI: it
/// asserted that a competitor could not take a 150ms lease while six 100ms sleeps elapsed, and one
/// scheduling hiccup on a loaded runner let the lease lapse between two renewals. It failed a PR
/// that never touched the gateway.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _start = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward, firing every timer that comes due on the way — each one with the
    /// clock reading ITS due time rather than the end of the jump, so a callback that schedules
    /// more work measures from the moment it actually ran. A timer re-armed inside a callback
    /// fires again within the same call when it falls due before the end of the jump.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(by), "Time does not run backwards.");

        var target = Interlocked.Read(ref _ticks) + by.Ticks;

        while (true)
        {
            ManualTimer? due;
            lock (_gate)
            {
                var next = _timers
                    .Select(timer => (Timer: timer, DueAt: timer.DueAtTicks))
                    .Where(candidate => candidate.DueAt <= target)
                    .OrderBy(candidate => candidate.DueAt)
                    .FirstOrDefault();

                if (next.Timer is null || next.DueAt is not { } dueAt)
                {
                    Interlocked.Exchange(ref _ticks, target);
                    return;
                }

                due = next.Timer;
                Interlocked.Exchange(ref _ticks, Math.Max(Interlocked.Read(ref _ticks), dueAt));
            }

            // Fired outside the lock: a callback may Change or Dispose its own timer, or create
            // another one, and all three take the same lock.
            due.Fire();
        }
    }

    internal long CurrentTicks => Interlocked.Read(ref _ticks);

    /// <summary>
    /// Completes once something is actually waiting on this clock.
    ///
    /// The other half of determinism, and the half that is easy to miss. <see cref="Advance"/>
    /// only fires timers that are ALREADY armed, while the loop under test arms its next delay on
    /// its own thread after the previous one came back. A test that advanced without waiting would
    /// sometimes move the clock past a delay that had not been scheduled yet, and then wait for a
    /// callback that was now due far in the future — a hang instead of a flake.
    /// </summary>
    public async Task WaitForArmedTimerAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_timers.Exists(timer => timer.DueAtTicks is not null)) return;
            }

            // A scheduling wait, not a wall-clock one: the arming is a continuation that is already
            // queued, so this only hands the thread over until it runs. Waiting LONGER here can
            // never change the outcome — which is exactly what the old real-clock test could not
            // say. The token is the test's own timeout for an arming that never comes.
            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    /// <summary>One scheduled callback. <c>DueAtTicks</c> is absolute, on this provider's clock.</summary>
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        /// <summary>Null means disarmed: an infinite due time, or a one-shot that has fired.</summary>
        public long? DueAtTicks { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                DueAtTicks = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.CurrentTicks + dueTime.Ticks;
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                // Re-armed BEFORE the callback runs. Task.Delay's callback completes a task whose
                // continuation can run inline and arm the next delay on this same provider, and a
                // period applied afterwards would overwrite that with a stale deadline.
                DueAtTicks = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan
                    ? owner.CurrentTicks + _period.Ticks
                    : null;
            }

            callback(state);
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
