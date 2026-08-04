using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Logging;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public sealed class PostgresUsageSettlementService : IUsageSettlementService
{
    private readonly IUsageSettlementRepository _repository;
    private readonly ILogger<PostgresUsageSettlementService> _logger;
    private readonly IBillingOperationalAlertService? _alertService;

    public PostgresUsageSettlementService(
        IUsageSettlementRepository repository,
        ILogger<PostgresUsageSettlementService> logger,
        IBillingOperationalAlertService? alertService = null)
    {
        _repository = repository;
        _logger = logger;
        _alertService = alertService;
    }

    public async Task<Result<SettleUsageChargeResult>> SettleUsageChargeAsync(
        SettleUsageChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.CreditsConsumed <= 0)
            return Result.Failure<SettleUsageChargeResult>("Credits consumed must be greater than zero.", ErrorCodes.ValidationError);

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Result.Failure<SettleUsageChargeResult>("Idempotency key is required.", ErrorCodes.ValidationError);

        try
        {
            var result = await _repository.ExecuteSettlementAsync(request, cancellationToken);
            if (result is null)
                return Result.Failure<SettleUsageChargeResult>("Usage settlement returned no result.", ErrorCodes.InternalServerError);

            return Result.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                BillingOperationalEventIds.SettlementFailed,
                ex,
                "Failed to settle usage charge. SubscriptionId={SubscriptionId}, ChargeType={ChargeType}, IdempotencyKey={IdempotencyKey}",
                request.SubscriptionId,
                request.ChargeType,
                request.IdempotencyKey);
            if (_alertService is not null)
                await _alertService.AlertSettlementFailedAsync(request, ex.Message, cancellationToken);

            return Result.Failure<SettleUsageChargeResult>(ex.Message, ErrorCodes.InternalServerError);
        }
    }
}
