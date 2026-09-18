using System;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// What the Cartesia usage sync last did, as the admin Insights snapshot reports it.
/// <paramref name="Status"/> is one of <see cref="Domain.Constants.ProviderUsageConstants.SyncStatuses"/>;
/// <paramref name="Message"/> says why for disabled / error, and never contains a key.
/// </summary>
public sealed record CartesiaUsageSyncState(
    string Status,
    bool FilteredToApiKey,
    DateTime? LastAttemptAt,
    DateTime? LastSuccessAt,
    string? Message);

/// <summary>
/// Process-wide state of the Cartesia usage sync, written by CartesiaUsageSyncWorker and read by the
/// Insights snapshot. Billing runs the worker and the API in one process, so a singleton is the
/// shared place; after a restart it starts from the configuration and the first sync fills it in.
/// </summary>
public interface ICartesiaUsageSyncStatus
{
    CartesiaUsageSyncState Current { get; }

    void MarkDisabled(string message);

    void MarkSucceeded(DateTime at);

    void MarkFailed(DateTime at, string message);
}
