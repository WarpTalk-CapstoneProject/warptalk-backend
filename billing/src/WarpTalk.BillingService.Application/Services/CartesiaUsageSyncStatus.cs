using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// In-process <see cref="ICartesiaUsageSyncStatus"/>: correct for one process, and the fallback the
/// Redis-backed status uses when Redis cannot be read.
/// </summary>
public sealed class CartesiaUsageSyncStatus : ICartesiaUsageSyncStatus
{
    public const string NotConfiguredMessage =
        "CARTESIA_ADMIN_API_KEY is not set, so Cartesia usage is not synced; dubbing cost is estimated from rate cards";

    private readonly object _gate = new();
    private CartesiaUsageSyncState _state;

    public CartesiaUsageSyncStatus(bool configured, bool filteredToApiKey)
    {
        _state = Initial(configured, filteredToApiKey);
    }

    public static CartesiaUsageSyncState Initial(bool configured, bool filteredToApiKey)
        => configured
            ? new CartesiaUsageSyncState(ProviderUsageConstants.SyncStatuses.Pending, filteredToApiKey, null, null, null)
            : new CartesiaUsageSyncState(ProviderUsageConstants.SyncStatuses.Disabled, filteredToApiKey, null, null, NotConfiguredMessage);

    public static CartesiaUsageSyncState Succeeded(CartesiaUsageSyncState state, DateTime at)
        => state with
        {
            Status = ProviderUsageConstants.SyncStatuses.Ok,
            LastAttemptAt = at,
            LastSuccessAt = at,
            Message = null,
            FailureStreak = 0,
        };

    public static CartesiaUsageSyncState Failed(CartesiaUsageSyncState state, DateTime at, string message)
        => state with
        {
            Status = ProviderUsageConstants.SyncStatuses.Error,
            LastAttemptAt = at,
            Message = message,
            FailureStreak = state.FailureStreak + 1,
        };

    public Task<CartesiaUsageSyncState> GetAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_state);
    }

    public Task MarkDisabledAsync(string message, CancellationToken ct = default)
    {
        lock (_gate) _state = _state with { Status = ProviderUsageConstants.SyncStatuses.Disabled, Message = message };
        return Task.CompletedTask;
    }

    public Task<CartesiaUsageSyncState> MarkSucceededAsync(DateTime at, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_state = Succeeded(_state, at));
    }

    public Task<CartesiaUsageSyncState> MarkFailedAsync(DateTime at, string message, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_state = Failed(_state, at, message));
    }
}
