using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class BillingPolicyRepository : IBillingPolicyRepository
{
    private readonly IUnitOfWork _unitOfWork;

    public BillingPolicyRepository(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }



    public async Task<decimal> ReadPolicyValueAsync(string key, decimal seedValue, CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);
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

    public async Task UpsertPolicyValueAsync(string key, decimal value, CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
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

    private async Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _unitOfWork.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
        return connection;
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
