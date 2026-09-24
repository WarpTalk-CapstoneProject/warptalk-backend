namespace WarpTalk.Gateway.Tests.Helpers;

/// <summary>A clock that only moves when the test says so. Covers wall time and timestamps.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;
    private readonly DateTimeOffset _start = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
