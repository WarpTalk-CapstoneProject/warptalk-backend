using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Options;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public sealed class BillingPolicyService : IBillingPolicyService
{
    private const string VatRateConfigKey = "vat_rate";

    private readonly BillingDbContext _dbContext;
    private readonly BillingPolicyOptions _seedPolicy;
    private readonly ILogger<BillingPolicyService> _logger;

    public BillingPolicyService(
        BillingDbContext dbContext,
        IOptions<BillingPolicyOptions> seedPolicy,
        ILogger<BillingPolicyService> logger)
    {
        _dbContext = dbContext;
        _seedPolicy = seedPolicy.Value;
        _logger = logger;
    }

    public async Task<BillingPolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        var connection = (System.Data.Common.DbConnection)_dbContext.Database.GetDbConnection();
        await EnsureOpenAsync(connection, cancellationToken);
        await EnsureTableExistsAsync(connection, cancellationToken);

        var vatRate = await ReadPolicyValueAsync(connection, VatRateConfigKey, _seedPolicy.VatRate!.Value, cancellationToken);

        return new BillingPolicyDto(vatRate);
    }

    public async Task<Result<BillingPolicyDto>> UpdatePolicyAsync(
        UpdateBillingPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.VatRate is < 0 or > 1)
        {
            return Result.Failure<BillingPolicyDto>(
                "Billing policy values are invalid.",
                ErrorCodes.ValidationError);
        }

        try
        {
            var connection = (System.Data.Common.DbConnection)_dbContext.Database.GetDbConnection();
            await EnsureOpenAsync(connection, cancellationToken);
            await EnsureTableExistsAsync(connection, cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await UpsertPolicyValueAsync(connection, transaction, VatRateConfigKey, request.VatRate, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Result.Success(new BillingPolicyDto(request.VatRate));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating billing policy");
            return Result.Failure<BillingPolicyDto>("Unable to update billing policy.", ErrorCodes.InternalServerError);
        }
    }

    private static async Task EnsureTableExistsAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE SCHEMA IF NOT EXISTS subscription;

            CREATE TABLE IF NOT EXISTS subscription.billing_policy_config (
                key VARCHAR(100) PRIMARY KEY,
                value NUMERIC(18, 6) NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
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

    private static async Task<decimal> ReadPolicyValueAsync(
        System.Data.Common.DbConnection connection,
        string key,
        decimal seedValue,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value
            FROM subscription.billing_policy_config
            WHERE key = @key
            """;
        AddParameter(command, "key", key);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null || value == DBNull.Value
            ? seedValue
            : Convert.ToDecimal(value);
    }

    private static async Task UpsertPolicyValueAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
        decimal value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO subscription.billing_policy_config (key, value, updated_at)
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
