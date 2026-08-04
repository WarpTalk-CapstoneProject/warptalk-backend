using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class UsageRateCardRepository : IUsageRateCardRepository
{
    private const string DefaultCurrency = "VND";
    private readonly IUnitOfWork _unitOfWork;
    private DbTransaction? _currentTransaction;

    public UsageRateCardRepository(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }



    public async Task<IReadOnlyList<UsageRateCardDto>> GetActiveRateCardsAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   charge_type,
                   COALESCE(unit, '') AS unit,
                   COALESCE(provider, '') AS provider,
                   COALESCE(model, '') AS model,
                   source_language_code,
                   target_language_code,
                   unit_price,
                   currency,
                   provider_unit_cost,
                   markup_multiplier,
                   effective_from,
                   effective_to,
                   is_active
            FROM subscription.usage_rate_card
            WHERE effective_to IS NULL
            ORDER BY charge_type, unit, provider, model, source_language_code NULLS LAST, target_language_code NULLS LAST
            """;

        var rows = new List<UsageRateCardDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRateCard(reader));
        }
        return rows;
    }

    public async Task<bool> RateCardIdentityExistsAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (_currentTransaction != null) command.Transaction = _currentTransaction;
        
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM subscription.usage_rate_card
                WHERE charge_type = @charge_type
                  AND unit = @unit
                  AND currency = @currency
                  AND provider = @provider
                  AND model = @model
                  AND source_language_code IS NOT DISTINCT FROM @source_language_code
                  AND target_language_code IS NOT DISTINCT FROM @target_language_code
            )
            """;

        AddParameter(command, "charge_type", request.ChargeType.Trim());
        AddParameter(command, "unit", request.Unit.Trim());
        AddParameter(command, "currency", NormalizeCurrency(request.Currency));
        AddParameter(command, "provider", request.Provider.Trim());
        AddParameter(command, "model", request.Model.Trim());
        AddParameter(command, "source_language_code", NormalizeLanguageCode(request.SourceLanguageCode) ?? (object)DBNull.Value);
        AddParameter(command, "target_language_code", NormalizeLanguageCode(request.TargetLanguageCode) ?? (object)DBNull.Value);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is bool exists && exists;
    }

    public async Task<UsageRateCardDto> UpsertRateCardAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);
        
        await using (var deactivate = connection.CreateCommand())
        {
            if (_currentTransaction != null) deactivate.Transaction = _currentTransaction;
            deactivate.CommandText = """
                UPDATE subscription.usage_rate_card
                   SET is_active = false,
                       effective_to = NOW()
                 WHERE is_active = true
                   AND effective_to IS NULL
                   AND charge_type = @charge_type
                   AND unit = @unit
                   AND currency = @currency
                   AND provider = @provider
                   AND model = @model
                   AND source_language_code IS NOT DISTINCT FROM @source_language_code
                   AND target_language_code IS NOT DISTINCT FROM @target_language_code
                """;

            AddParameter(deactivate, "charge_type", request.ChargeType.Trim());
            AddParameter(deactivate, "unit", request.Unit.Trim());
            AddParameter(deactivate, "currency", NormalizeCurrency(request.Currency));
            AddParameter(deactivate, "provider", request.Provider.Trim());
            AddParameter(deactivate, "model", request.Model.Trim());
            AddParameter(deactivate, "source_language_code", NormalizeLanguageCode(request.SourceLanguageCode) ?? (object)DBNull.Value);
            AddParameter(deactivate, "target_language_code", NormalizeLanguageCode(request.TargetLanguageCode) ?? (object)DBNull.Value);
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            if (_currentTransaction != null) insert.Transaction = _currentTransaction;
            insert.CommandText = """
                INSERT INTO subscription.usage_rate_card (
                    id,
                    charge_type,
                    unit,
                    currency,
                    provider,
                    model,
                    source_language_code,
                    target_language_code,
                    provider_unit_cost,
                    markup_multiplier,
                    unit_price,
                    effective_from,
                    is_active,
                    notes
                )
                VALUES (
                    uuidv7(),
                    @charge_type,
                    @unit,
                    @currency,
                    @provider,
                    @model,
                    @source_language_code,
                    @target_language_code,
                    @provider_unit_cost,
                    @markup_multiplier,
                    @unit_price,
                    NOW(),
                    @is_active,
                    'Updated from admin pricing controls'
                )
                RETURNING id,
                          charge_type,
                          unit,
                          provider,
                          model,
                          source_language_code,
                          target_language_code,
                          unit_price,
                          currency,
                          provider_unit_cost,
                          markup_multiplier,
                          effective_from,
                          effective_to,
                          is_active
                """;

            AddParameter(insert, "charge_type", request.ChargeType.Trim());
            AddParameter(insert, "unit", request.Unit.Trim());
            AddParameter(insert, "currency", NormalizeCurrency(request.Currency));
            AddParameter(insert, "provider", request.Provider.Trim());
            AddParameter(insert, "model", request.Model.Trim());
            AddParameter(insert, "source_language_code", NormalizeLanguageCode(request.SourceLanguageCode) ?? (object)DBNull.Value);
            AddParameter(insert, "target_language_code", NormalizeLanguageCode(request.TargetLanguageCode) ?? (object)DBNull.Value);
            AddParameter(insert, "provider_unit_cost", request.ProviderUnitCostUsd ?? (object)DBNull.Value);
            AddParameter(insert, "markup_multiplier", request.MarkupMultiplier ?? (object)DBNull.Value);
            AddParameter(insert, "unit_price", request.UnitPrice);
            AddParameter(insert, "is_active", request.IsActive ?? true);

            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Unable to create usage rate card.");

            return ReadRateCard(reader);
        }
    }

    public async Task<decimal> ReadPricingConfigValueAsync(string key, decimal defaultValue, CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (_currentTransaction != null) command.Transaction = _currentTransaction;
        
        command.CommandText = """
            SELECT value
            FROM subscription.billing_pricing_config
            WHERE key = @key
            """;
        AddParameter(command, "key", key);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null || value == DBNull.Value
            ? defaultValue
            : Convert.ToDecimal(value);
    }

    public async Task UpsertPricingConfigValueAsync(string key, decimal value, CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (_currentTransaction != null) command.Transaction = _currentTransaction;
        
        command.CommandText = """
            INSERT INTO subscription.billing_pricing_config (key, value, updated_at)
            VALUES (@key, @value, NOW())
            ON CONFLICT (key)
            DO UPDATE SET value = EXCLUDED.value,
                          updated_at = NOW()
            """;
        AddParameter(command, "key", key);
        AddParameter(command, "value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);
        _currentTransaction = await connection.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.CommitAsync(cancellationToken);
            _currentTransaction.Dispose();
            _currentTransaction = null;
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.RollbackAsync(cancellationToken);
            _currentTransaction.Dispose();
            _currentTransaction = null;
        }
    }

    private async Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _unitOfWork.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
        return connection;
    }

    private static UsageRateCardDto ReadRateCard(IDataRecord reader)
    {
        return new UsageRateCardDto(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("charge_type")),
            reader.GetString(reader.GetOrdinal("unit")),
            reader.GetString(reader.GetOrdinal("provider")),
            reader.GetString(reader.GetOrdinal("model")),
            ReadNullableString(reader, "source_language_code"),
            ReadNullableString(reader, "target_language_code"),
            reader.GetDecimal(reader.GetOrdinal("unit_price")),
            reader.GetString(reader.GetOrdinal("currency")),
            ReadNullableDecimal(reader, "provider_unit_cost"),
            ReadNullableDecimal(reader, "markup_multiplier"),
            reader.GetDateTime(reader.GetOrdinal("effective_from")),
            reader.IsDBNull(reader.GetOrdinal("effective_to")) ? null : reader.GetDateTime(reader.GetOrdinal("effective_to")),
            reader.GetBoolean(reader.GetOrdinal("is_active")));
    }

    private static decimal? ReadNullableDecimal(IDataRecord reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }

    private static string? ReadNullableString(IDataRecord reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string? NormalizeLanguageCode(string? languageCode)
    {
        return string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().ToLowerInvariant();
    }

    private static string NormalizeCurrency(string? currency)
    {
        return string.IsNullOrWhiteSpace(currency)
            ? DefaultCurrency
            : currency.Trim().ToUpperInvariant();
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
