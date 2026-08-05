using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

/// <summary>
/// APPROVED RAW-SQL PRIMITIVE — do not "clean up" into EF Core LINQ.
///
/// ClaimAsync is the outbox dispatcher's row-claim primitive. Several dispatcher
/// instances poll the same table concurrently, and correctness depends on
/// FOR UPDATE SKIP LOCKED: each instance claims a disjoint batch, and rows locked
/// by a peer are skipped instead of blocking. EF Core cannot express SKIP LOCKED,
/// so a LINQ rewrite would either serialise the dispatchers behind each other or
/// let two instances claim the same row and publish the event twice.
///
/// The claim is also a single statement — the UPDATE that stamps locked_at and
/// the SELECT that chooses the rows must not be separable, or a row can be
/// selected by one instance and stamped by another.
///
/// PurgePublishedBeforeAsync is a set-based DELETE kept here for the same reason
/// it always was: it must not load rows into the change tracker to delete them.
///
/// Counterpart primitive: <see cref="UsageSettlementRepository"/>.
/// Both are allowlisted by warptalk-infrastructure/scripts/check-production-deployment.sh.
/// </summary>
public sealed class OutboxClaimStore(BillingDbContext context) : IOutboxClaimStore
{
    public async Task<int> PurgePublishedBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            DELETE FROM subscription.outbox_messages
            WHERE published_at IS NOT NULL
              AND published_at < @cutoff_utc;
            """,
            connection,
            GetCurrentTransaction());
        command.Parameters.AddWithValue("cutoff_utc", cutoffUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken);

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
            connection,
            GetCurrentTransaction());
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

    private async Task<NpgsqlConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// Enlists in the caller's EF transaction when one is open. Without this the
    /// claim would commit independently of the surrounding unit of work, so a
    /// rolled-back dispatch could leave rows stamped as locked.
    /// </summary>
    private NpgsqlTransaction? GetCurrentTransaction()
        => context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
}
