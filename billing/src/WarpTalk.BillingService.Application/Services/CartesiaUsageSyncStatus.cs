using System;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Services;

/// <inheritdoc cref="ICartesiaUsageSyncStatus"/>
public sealed class CartesiaUsageSyncStatus : ICartesiaUsageSyncStatus
{
    public const string NotConfiguredMessage =
        "CARTESIA_ADMIN_API_KEY is not set, so Cartesia usage is not synced; dubbing cost is estimated from rate cards";

    private readonly object _gate = new();
    private CartesiaUsageSyncState _state;

    public CartesiaUsageSyncStatus(bool configured, bool filteredToApiKey)
    {
        _state = configured
            ? new CartesiaUsageSyncState(ProviderUsageConstants.SyncStatuses.Pending, filteredToApiKey, null, null, null)
            : new CartesiaUsageSyncState(ProviderUsageConstants.SyncStatuses.Disabled, filteredToApiKey, null, null, NotConfiguredMessage);
    }

    public CartesiaUsageSyncState Current
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public void MarkDisabled(string message)
    {
        lock (_gate) _state = _state with { Status = ProviderUsageConstants.SyncStatuses.Disabled, Message = message };
    }

    public void MarkSucceeded(DateTime at)
    {
        lock (_gate)
        {
            _state = _state with
            {
                Status = ProviderUsageConstants.SyncStatuses.Ok,
                LastAttemptAt = at,
                LastSuccessAt = at,
                Message = null,
            };
        }
    }

    public void MarkFailed(DateTime at, string message)
    {
        lock (_gate)
        {
            _state = _state with
            {
                Status = ProviderUsageConstants.SyncStatuses.Error,
                LastAttemptAt = at,
                Message = message,
            };
        }
    }
}
