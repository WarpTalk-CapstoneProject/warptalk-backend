using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Outbox;

public sealed class WorkspaceOutboxDispatcher(
    WorkspaceDbContext dbContext,
    WorkspaceOutboxDelivery delivery,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<int> PurgePublishedBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM workspace.outbox_messages
            WHERE published_at IS NOT NULL
              AND published_at < {cutoffUtc};
            """,
            cancellationToken);

    public async Task<int> DispatchPendingAsync(
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            return 0;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var messages = await ClaimAsync(batchSize, now, cancellationToken);
        var publishedCount = 0;

        foreach (var message in messages)
        {
            var messageNow = _timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                await delivery.PublishAsync(message, cancellationToken);
                message.PublishedAt = messageNow;
                message.LastError = null;
                publishedCount++;
                WorkspaceOutboxMetrics.Published.Add(
                    1,
                    new KeyValuePair<string, object?>("event.type", message.EventType));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.LastError = exception.Message;
                WorkspaceOutboxMetrics.Failed.Add(
                    1,
                    new KeyValuePair<string, object?>("event.type", message.EventType));
                if (message.AttemptCount >= 10)
                {
                    message.DeadLetteredAt = messageNow;
                    WorkspaceOutboxMetrics.DeadLettered.Add(
                        1,
                        new KeyValuePair<string, object?>("event.type", message.EventType));
                }
                else
                    message.AvailableAt = messageNow + RetryDelay(message.AttemptCount);
            }
            finally
            {
                message.LockedAt = null;
            }
        }

        if (messages.Count > 0)
            await dbContext.SaveChangesAsync(cancellationToken);

        return publishedCount;
    }

    private async Task<IReadOnlyList<WorkspaceOutboxMessage>> ClaimAsync(
        int batchSize,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            UPDATE workspace.outbox_messages AS target
            SET locked_at = @now_utc,
                attempt_count = target.attempt_count + 1
            FROM (
                SELECT id
                FROM workspace.outbox_messages
                WHERE published_at IS NULL
                  AND dead_lettered_at IS NULL
                  AND available_at <= @now_utc
                  AND (locked_at IS NULL OR locked_at < @lock_cutoff)
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT @batch_size
            ) AS pending
            WHERE target.id = pending.id
            RETURNING target.id, target.event_type, target.compatibility_event_type,
                      target.schema_version, target.occurred_at, target.producer,
                      target.correlation_id, target.causation_id, target.workspace_id,
                      target.payload_json, target.attempt_count, target.available_at,
                      target.published_at, target.locked_at, target.dead_lettered_at,
                      target.last_error, target.created_at;
            """,
            connection);
        command.Parameters.AddWithValue("now_utc", now);
        command.Parameters.AddWithValue("lock_cutoff", now - LockTimeout);
        command.Parameters.AddWithValue("batch_size", batchSize);

        var messages = new List<WorkspaceOutboxMessage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new WorkspaceOutboxMessage
                {
                    Id = reader.GetGuid(0),
                    EventType = reader.GetString(1),
                    CompatibilityEventType = reader.GetString(2),
                    SchemaVersion = reader.GetInt32(3),
                    OccurredAt = reader.GetDateTime(4),
                    Producer = reader.GetString(5),
                    CorrelationId = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CausationId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    WorkspaceId = reader.IsDBNull(8) ? null : reader.GetGuid(8),
                    PayloadJson = reader.GetString(9),
                    AttemptCount = reader.GetInt32(10),
                    AvailableAt = reader.GetDateTime(11),
                    PublishedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                    LockedAt = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                    DeadLetteredAt = reader.IsDBNull(14) ? null : reader.GetDateTime(14),
                    LastError = reader.IsDBNull(15) ? null : reader.GetString(15),
                    CreatedAt = reader.GetDateTime(16)
                });
            }
        }

        dbContext.AttachRange(messages);
        return messages;
    }

    private static TimeSpan RetryDelay(int attemptCount)
    {
        var seconds = Math.Min(
            300,
            Math.Pow(2, Math.Max(0, attemptCount - 1)) * 5);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromSeconds(seconds * jitter);
    }
}
