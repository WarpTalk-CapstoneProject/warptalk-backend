using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Infrastructure.Logging;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public sealed class BillingOperationalAlertService : IBillingOperationalAlertService
{
    private readonly ILogger<BillingOperationalAlertService> _logger;

    public BillingOperationalAlertService(ILogger<BillingOperationalAlertService> logger)
    {
        _logger = logger;
    }

    public Task<Result> AlertSettlementFailedAsync(
        SettleUsageChargeRequest request,
        string? error,
        CancellationToken cancellationToken = default)
    {
        _logger.LogError(
            BillingOperationalEventIds.SettlementFailed,
            "billing_settlement_failed SubscriptionId={SubscriptionId}, WorkspaceId={WorkspaceId}, ChargeType={ChargeType}, Unit={Unit}, IdempotencyKey={IdempotencyKey}, Error={Error}",
            request.SubscriptionId,
            request.WorkspaceId,
            request.ChargeType,
            request.Unit,
            request.IdempotencyKey,
            error);

        return Task.FromResult(Result.Success());
    }
}
