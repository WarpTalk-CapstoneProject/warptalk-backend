using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public sealed class UsageRateCardAdminService : IUsageRateCardAdminService
{
    private const decimal DefaultFxRateUsdVnd = 26300m;
    private const decimal DefaultCreditValueVnd = 4m;
    private const string DefaultCurrency = "VND";
    private const string FxRateConfigKey = "fx_rate_usd_vnd";
    private const string CreditValueConfigKey = "credit_value_vnd";
    private const string PricingFormula = "provider_unit_cost_usd * fx_rate_usd_vnd * markup_multiplier / credit_value_vnd";
    private const string ResolverKey = "provider + model + charge_type + unit + source_language_code + target_language_code";

    private readonly BillingDbContext _dbContext;
    private readonly ILogger<UsageRateCardAdminService> _logger;

    public UsageRateCardAdminService(
        BillingDbContext dbContext,
        ILogger<UsageRateCardAdminService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<UsageRateCardDto>>> GetActiveRateCardsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = (System.Data.Common.DbConnection)_dbContext.Database.GetDbConnection();
            await EnsureOpenAsync(connection, cancellationToken);
            await EnsureTablesExistAsync(connection, cancellationToken);

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

            return Result.Success<IReadOnlyList<UsageRateCardDto>>(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading usage rate card");
            return Result.Failure<IReadOnlyList<UsageRateCardDto>>("Unable to load usage rate card.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<UsageRateCardDto>> UpsertRateCardAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsValid(request))
            return Result.Failure<UsageRateCardDto>("Invalid usage rate card request.", ErrorCodes.ValidationError);

        try
        {
            var connection = (System.Data.Common.DbConnection)_dbContext.Database.GetDbConnection();
            await EnsureOpenAsync(connection, cancellationToken);
            await EnsureTablesExistAsync(connection, cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var identityExists = await RateCardIdentityExistsAsync(
                connection,
                transaction,
                request,
                cancellationToken);
            if (!identityExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<UsageRateCardDto>(
                    "Usage rate-card identity is not registered. Add new billing identities through a migration/backend release first.",
                    ErrorCodes.ValidationError);
            }

            await using (var deactivate = connection.CreateCommand())
            {
                deactivate.Transaction = transaction;
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

            UsageRateCardDto inserted;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
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
                    return Result.Failure<UsageRateCardDto>("Unable to create usage rate card.", ErrorCodes.InternalServerError);

                inserted = ReadRateCard(reader);
            }

            await transaction.CommitAsync(cancellationToken);
            return Result.Success(inserted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating usage rate card");
            return Result.Failure<UsageRateCardDto>("Unable to update usage rate card.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PricingConfigDto>> GetPricingConfigAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = (System.Data.Common.DbConnection)_dbContext.Database.GetDbConnection();
            await EnsureOpenAsync(connection, cancellationToken);
            await EnsureTablesExistAsync(connection, cancellationToken);

            var fxRate = await ReadPricingConfigValueAsync(connection, FxRateConfigKey, DefaultFxRateUsdVnd, cancellationToken);
            var creditValue = await ReadPricingConfigValueAsync(connection, CreditValueConfigKey, DefaultCreditValueVnd, cancellationToken);

            return Result.Success(CreatePricingConfig(fxRate, creditValue));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load billing pricing config; using defaults");
            return Result.Success(CreatePricingConfig(DefaultFxRateUsdVnd, DefaultCreditValueVnd));
        }
    }

    public async Task<Result<PricingConfigDto>> UpdatePricingConfigAsync(UpdatePricingConfigRequest request, CancellationToken cancellationToken = default)
    {
        if (request.FxRateUsdVnd <= 0 || request.CreditValueVnd <= 0)
            return Result.Failure<PricingConfigDto>("Pricing config values must be positive.", ErrorCodes.ValidationError);

        try
        {
            var connection = (System.Data.Common.DbConnection)_dbContext.Database.GetDbConnection();
            await EnsureOpenAsync(connection, cancellationToken);
            await EnsureTablesExistAsync(connection, cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await UpsertPricingConfigValueAsync(connection, transaction, FxRateConfigKey, request.FxRateUsdVnd, cancellationToken);
            await UpsertPricingConfigValueAsync(connection, transaction, CreditValueConfigKey, request.CreditValueVnd, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Result.Success(CreatePricingConfig(request.FxRateUsdVnd, request.CreditValueVnd));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating billing pricing config");
            return Result.Failure<PricingConfigDto>("Unable to update billing pricing config.", ErrorCodes.InternalServerError);
        }
    }

    private static async Task EnsureTablesExistAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE SCHEMA IF NOT EXISTS subscription;

            CREATE TABLE IF NOT EXISTS subscription.billing_pricing_config (
                key VARCHAR(100) PRIMARY KEY,
                value NUMERIC(18, 6) NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS subscription.usage_rate_card (
                id UUID PRIMARY KEY DEFAULT uuidv7(),
                charge_type VARCHAR(50) NOT NULL,
                unit VARCHAR(20) NOT NULL,
                currency VARCHAR(3) NOT NULL DEFAULT 'VND',
                provider VARCHAR(50) NOT NULL,
                model VARCHAR(50) NOT NULL,
                source_language_code VARCHAR(10),
                target_language_code VARCHAR(10),
                provider_unit_cost NUMERIC(18, 12),
                markup_multiplier NUMERIC(8, 4),
                unit_price NUMERIC(18, 6) NOT NULL,
                effective_from TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                effective_to TIMESTAMPTZ,
                is_active BOOLEAN NOT NULL DEFAULT TRUE,
                notes TEXT
            );

            ALTER TABLE subscription.usage_rate_card
                ADD COLUMN IF NOT EXISTS source_language_code VARCHAR(10),
                ADD COLUMN IF NOT EXISTS target_language_code VARCHAR(10);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureOpenAsync(IDbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Open)
            return;

        if (connection is System.Data.Common.DbConnection dbConnection)
            await dbConnection.OpenAsync(cancellationToken);
        else
            connection.Open();
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

    private static bool IsValid(UpsertUsageRateCardRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.ChargeType) &&
               !string.IsNullOrWhiteSpace(request.Unit) &&
               !string.IsNullOrWhiteSpace(request.Provider) &&
               !string.IsNullOrWhiteSpace(request.Model) &&
               request.UnitPrice >= 0 &&
               (request.ProviderUnitCostUsd is null or >= 0) &&
               (request.MarkupMultiplier is null or >= 0);
    }

    private static async Task<bool> RateCardIdentityExistsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        UpsertUsageRateCardRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static PricingConfigDto CreatePricingConfig(decimal fxRateUsdVnd, decimal creditValueVnd)
    {
        return new PricingConfigDto(fxRateUsdVnd, creditValueVnd, PricingFormula, ResolverKey);
    }

    private static async Task<decimal> ReadPricingConfigValueAsync(
        System.Data.Common.DbConnection connection,
        string key,
        decimal defaultValue,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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

    private static async Task UpsertPricingConfigValueAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
        decimal value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
