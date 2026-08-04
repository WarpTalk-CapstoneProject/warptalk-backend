using System;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class UsageSettlementRepository : IUsageSettlementRepository
{
    private readonly IUnitOfWork _unitOfWork;

    public UsageSettlementRepository(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<SettleUsageChargeResult?> ExecuteSettlementAsync(
        SettleUsageChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = _unitOfWork.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT applied, transaction_id, usage_record_id, balance_after, service_state, suspended_reason, just_entered_overage
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
            return null;

        return new SettleUsageChargeResult(
            Applied: reader.GetBoolean(0),
            TransactionId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
            UsageRecordId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
            BalanceAfter: reader.IsDBNull(3) ? null : reader.GetInt32(3),
            ServiceState: reader.IsDBNull(4) ? null : reader.GetString(4),
            SuspendedReason: reader.IsDBNull(5) ? null : reader.GetString(5),
            JustEnteredOverage: reader.GetBoolean(6));
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
