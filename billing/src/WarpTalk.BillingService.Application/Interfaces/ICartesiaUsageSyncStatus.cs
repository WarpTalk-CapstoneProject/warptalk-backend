using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// What the Cartesia usage sync last did, as the admin Insights snapshot reports it.
/// <paramref name="Status"/> is one of <see cref="Domain.Constants.ProviderUsageConstants.SyncStatuses"/>;
/// <paramref name="Message"/> says why for disabled / error, and never contains a key.
/// <paramref name="FailureStreak"/> counts consecutive failed syncs; it drives the shared backoff.
/// </summary>
public sealed record CartesiaUsageSyncState(
    string Status,
    bool FilteredToApiKey,
    DateTime? LastAttemptAt,
    DateTime? LastSuccessAt,
    string? Message,
    int FailureStreak = 0);

/// <summary>
/// State of the Cartesia usage sync, written by whichever billing replica ran the last sync and read
/// by every replica that serves the Insights snapshot. Production keeps it in Redis (billing runs
/// several replicas, and only one of them syncs at a time); tests use the in-memory one.
/// </summary>
public interface ICartesiaUsageSyncStatus
{
    Task<CartesiaUsageSyncState> GetAsync(CancellationToken ct = default);

    Task MarkDisabledAsync(string message, CancellationToken ct = default);

    Task<CartesiaUsageSyncState> MarkSucceededAsync(DateTime at, CancellationToken ct = default);

    Task<CartesiaUsageSyncState> MarkFailedAsync(DateTime at, string message, CancellationToken ct = default);
}
