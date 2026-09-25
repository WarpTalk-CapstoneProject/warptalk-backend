using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.Infrastructure.Clients;

/// <summary>
/// Writes billing's admin actions into the platform audit log over gRPC — the same contract auth
/// and translation-room use. Every failure comes back as a failed <see cref="Result"/>; nothing is
/// swallowed, because the caller abandons its change when the record fails.
/// </summary>
public sealed class AdminAuditGrpcClient : IAdminAuditRecorder
{
    private readonly AdminAuditService.AdminAuditServiceClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdminAuditGrpcClient> _logger;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public AdminAuditGrpcClient(
        AdminAuditService.AdminAuditServiceClient client,
        ILogger<AdminAuditGrpcClient> logger,
        TimeProvider? timeProvider = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _client = client;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<Result> RecordAsync(
        string action,
        string entityType,
        Guid? entityId,
        Guid workspaceId,
        Guid actorId,
        string reason,
        string correlationId,
        IReadOnlyDictionary<string, string?>? beforeSummary = null,
        IReadOnlyDictionary<string, string?>? afterSummary = null,
        bool succeeded = true,
        CancellationToken ct = default)
    {
        var request = new RecordAdminActionRequest
        {
            SourceService = AdminAuditSources.BillingService,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString() ?? string.Empty,
            WorkspaceId = workspaceId.ToString(),
            ActorId = actorId.ToString(),
            Reason = reason,
            Result = succeeded ? AdminAuditResults.Succeeded : AdminAuditResults.Failed,
            // The only failed entry this recorder writes is a change recorded and then not saved.
            ErrorMessage = succeeded ? string.Empty : "The change was recorded but could not be saved, so nothing changed.",
            PerformedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            CorrelationId = correlationId,
        };

        Fill(request.BeforeSummary, beforeSummary);
        Fill(request.AfterSummary, afterSummary);

        // Who asked and from where, read from the admin's own request here — the store only ever
        // sees this service's gRPC call.
        AdminAuditRequestMetadata.FromHttpContext(_httpContextAccessor?.HttpContext).ApplyTo(request);

        try
        {
            var response = await _client.RecordAdminActionAsync(request, cancellationToken: ct);
            if (response.Recorded)
            {
                return Result.Success();
            }

            _logger.LogError(
                "Admin audit refused. Action: {Action}, WorkspaceId: {WorkspaceId}, Reason: {Error}",
                action, workspaceId, response.ErrorMessage);
            return Result.Failure(
                "The change was not made because it could not be recorded in the audit log.",
                ErrorCodes.InternalServerError);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Admin audit call failed. Action: {Action}, Status: {Status}", action, ex.Status);
            return Result.Failure(
                "The change was not made because the audit log could not be reached.",
                ErrorCodes.InternalServerError);
        }
    }

    /// <summary>proto3 maps hold no nulls, so a null value is dropped rather than written as "".</summary>
    private static void Fill(
        Google.Protobuf.Collections.MapField<string, string> target,
        IReadOnlyDictionary<string, string?>? source)
    {
        if (source is null) return;
        foreach (var (key, value) in source)
        {
            if (value is not null) target[key] = value;
        }
    }
}
