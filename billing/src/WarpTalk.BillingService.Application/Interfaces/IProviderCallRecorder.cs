namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// Counts one call WarpTalk made to (or received from) an external provider, into the same
/// per-UTC-day Redis hash the AI workers write (warptalk:provider_calls:{day}); the billing sync
/// copies it into provider_call_stats. Fire and forget: never throws, never waits on Redis.
/// </summary>
public interface IProviderCallRecorder
{
    void Record(string provider, string operation, string outcome, long? latencyMs, string? model = null);
}
