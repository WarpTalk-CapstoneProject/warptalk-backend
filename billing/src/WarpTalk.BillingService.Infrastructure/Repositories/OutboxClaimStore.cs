using System.Data;
using Npgsql;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public sealed class OutboxClaimStore(IUnitOfWork unitOfWork) : IOutboxClaimStore
{
    public async Task<int> PurgePublishedBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)unitOfWork.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            DELETE FROM subscription.outbox_messages
            WHERE published_at IS NOT NULL
              AND published_at < @cutoff_utc;
            """,
            connection);
        command.Parameters.AddWithValue("cutoff_utc", cutoffUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)unitOfWork.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            UPDATE subscription.outbox_messages AS target
            SET locked_at = @now_utc,
                attempt_count = target.attempt_count + 1
            FROM (
                SELECT id
                FROM subscription.outbox_messages
                WHERE published_at IS NULL
                  AND dead_lettered_at IS NULL
                  AND available_at <= @now_utc
                  AND (locked_at IS NULL OR locked_at < @lock_cutoff)
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT @batch_size
            ) AS pending
            WHERE target.id = pending.id
            RETURNING target.id, target.event_type, target.schema_version,
                      target.occurred_at, target.producer, target.correlation_id,
                      target.causation_id, target.workspace_id, target.payload_json,
                      target.attempt_count, target.available_at, target.published_at,
                      target.locked_at, target.dead_lettered_at, target.last_error, target.created_at;
            """,
            connection);
        command.Parameters.AddWithValue("now_utc", nowUtc);
        command.Parameters.AddWithValue("lock_cutoff", nowUtc.AddMinutes(-5));
        command.Parameters.AddWithValue("batch_size", batchSize);

        var result = new List<OutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new OutboxMessage
            {
                Id = reader.GetGuid(0),
                EventType = reader.GetString(1),
                SchemaVersion = reader.GetInt32(2),
                OccurredAt = reader.GetDateTime(3),
                Producer = reader.GetString(4),
                CorrelationId = reader.IsDBNull(5) ? null : reader.GetString(5),
                CausationId = reader.IsDBNull(6) ? null : reader.GetString(6),
                WorkspaceId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
                PayloadJson = reader.GetString(8),
                AttemptCount = reader.GetInt32(9),
                AvailableAt = reader.GetDateTime(10),
                PublishedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                LockedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                DeadLetteredAt = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                LastError = reader.IsDBNull(14) ? null : reader.GetString(14),
                CreatedAt = reader.GetDateTime(15)
            });
        }

        return result;
    }
}
