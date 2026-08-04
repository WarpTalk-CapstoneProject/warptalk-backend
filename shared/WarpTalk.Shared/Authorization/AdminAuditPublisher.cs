using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Events;

namespace WarpTalk.Shared.Authorization;

/// <summary>
/// How a service records an administrative mutation in the platform audit log (WT-210).
/// </summary>
public interface IAdminAuditPublisher
{
    Task PublishAsync(
        string action,
        string entityType,
        Guid? entityId,
        AdminActorContext actor,
        string reason,
        Guid? workspaceId = null,
        string result = AdminAuditResults.Succeeded,
        IReadOnlyDictionary<string, string?>? beforeSummary = null,
        IReadOnlyDictionary<string, string?>? afterSummary = null,
        CancellationToken ct = default);
}

/// <summary>
/// Publishes admin.action_recorded onto the bus, where the workspace service appends it to the
/// append-only store.
/// </summary>
/// <remarks>
/// A failure to publish is logged but never thrown: the administrative action itself has
/// already been committed, and failing the caller's request afterwards would leave the
/// operator believing nothing happened. The gap is visible in the broker's error queue.
/// </remarks>
public sealed class AdminAuditPublisher : IAdminAuditPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<AdminAuditPublisher> _logger;
    private readonly string _sourceService;
    private readonly TimeProvider _timeProvider;

    public AdminAuditPublisher(
        IPublishEndpoint publishEndpoint,
        ILogger<AdminAuditPublisher> logger,
        string sourceService,
        TimeProvider? timeProvider = null)
    {
        _publishEndpoint = publishEndpoint;
        _logger = logger;
        _sourceService = sourceService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task PublishAsync(
        string action,
        string entityType,
        Guid? entityId,
        AdminActorContext actor,
        string reason,
        Guid? workspaceId = null,
        string result = AdminAuditResults.Succeeded,
        IReadOnlyDictionary<string, string?>? beforeSummary = null,
        IReadOnlyDictionary<string, string?>? afterSummary = null,
        CancellationToken ct = default)
    {
        try
        {
            await _publishEndpoint.Publish(
                new AdminActionRecordedEvent(
                    _sourceService,
                    action,
                    entityType,
                    entityId,
                    workspaceId,
                    actor.ActorId,
                    reason,
                    result,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    actor.CorrelationId,
                    // Redacted here as well as on the consumer, so a secret never reaches the
                    // broker in the first place.
                    AdminAuditRedaction.Redact(beforeSummary),
                    AdminAuditRedaction.Redact(afterSummary)),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to publish admin audit entry. Action: {Action}, Entity: {EntityType}/{EntityId}, Actor: {ActorId}",
                action,
                entityType,
                entityId,
                actor.ActorId);
        }
    }
}
