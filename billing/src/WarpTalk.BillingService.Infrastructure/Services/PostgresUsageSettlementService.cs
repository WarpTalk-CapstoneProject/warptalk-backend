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
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PostgresUsageSettlementService> _logger;
    private readonly IBillingOperationalAlertService? _alertService;

    public PostgresUsageSettlementService(
        IUnitOfWork unitOfWork,
        ILogger<PostgresUsageSettlementService> logger,
        IBillingOperationalAlertService? alertService = null)
    {
        _unitOfWork = unitOfWork;
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
            var connection = _unitOfWork.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT applied, transaction_id, usage_record_id, balance_after, service_state, suspended_reason
                FROM subscription.settle_usage_charge(
                    @subscription_id,
                    @user_id,
                    @workspace_id,
                    @usage_type,
                    @charge_type,
                    @reference_id,
                    @reference_type,
                    @translation_room_id,
                    @transcript_segment_id,
                    @quantity,
                    @unit,
                    @credits_consumed,
                    @idempotency_key,
                    @pricing_rate_card_id,
                    @unit_price_snapshot,
                    @currency,
                    @details::jsonb
                )
                """;

            AddParameter(command, "subscription_id", request.SubscriptionId);
            AddParameter(command, "user_id", request.UserId);
            AddParameter(command, "workspace_id", request.WorkspaceId);
            AddParameter(command, "usage_type", request.UsageType);
            AddParameter(command, "charge_type", request.ChargeType);
            AddParameter(command, "reference_id", request.ReferenceId);
            AddParameter(command, "reference_type", request.ReferenceType);
            AddParameter(command, "translation_room_id", request.TranslationRoomId);
            AddParameter(command, "transcript_segment_id", request.TranscriptSegmentId);
            AddParameter(command, "quantity", request.Quantity);
            AddParameter(command, "unit", request.Unit);
            AddParameter(command, "credits_consumed", request.CreditsConsumed);
            AddParameter(command, "idempotency_key", request.IdempotencyKey);
            AddParameter(command, "pricing_rate_card_id", request.PricingRateCardId);
            AddParameter(command, "unit_price_snapshot", request.UnitPriceSnapshot);
            AddParameter(command, "currency", request.Currency);
            AddParameter(command, "details", NormalizeJson(request.Details));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Result.Failure<SettleUsageChargeResult>("Usage settlement returned no result.", ErrorCodes.InternalServerError);

            var result = new SettleUsageChargeResult(
                Applied: reader.GetBoolean(0),
                TransactionId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
                UsageRecordId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                BalanceAfter: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                ServiceState: reader.IsDBNull(4) ? null : reader.GetString(4),
                SuspendedReason: reader.IsDBNull(5) ? null : reader.GetString(5));

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

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeJson(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return "{}";

        try
        {
            JsonDocument.Parse(details);
            return details;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { raw = details });
        }
    }
}
