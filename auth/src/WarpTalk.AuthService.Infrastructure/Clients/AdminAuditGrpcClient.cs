using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AuthService.Infrastructure.Clients;

/// <summary>
/// Writes auth's admin actions into the platform audit log, which the workspace service owns.
///
/// Every failure — a refused record, an RPC error, the workspace service being down — comes back
/// as a failed <see cref="Result"/>. Nothing is swallowed, because the caller's response to this
/// failing is to abandon the action rather than to log and continue. That is the whole reason
/// this is a gRPC call and not a publish.
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

    public Task<Result> RecordAsync(
        string action,
        Guid entityId,
        Guid actorId,
        string reason,
        string correlationId,
        Guid? workspaceId,
        IReadOnlyDictionary<string, string?>? beforeSummary = null,
        IReadOnlyDictionary<string, string?>? afterSummary = null,
        CancellationToken ct = default)
    {
        var request = NewRequest(action, AdminAuditEntityTypes.User, entityId, actorId, reason, correlationId);
        // Auth actions are not scoped to a workspace, except the sign-outs the admin workspace
        // page makes, which name the workspace in their route so they appear on its timeline.
        // Otherwise empty rather than something plausible: a wrong workspace id would file the
        // entry under a tenant that had nothing to do with it.
        request.WorkspaceId = workspaceId?.ToString() ?? string.Empty;
        Fill(request.BeforeSummary, beforeSummary);
        Fill(request.AfterSummary, afterSummary);
        return SendAsync(request, action, entityId, ct);
    }

    public Task<Result> RecordSubjectAsync(AdminAuditSubjectRecord record, CancellationToken ct = default)
    {
        var request = NewRequest(
            record.Action, record.EntityType, record.EntityId, record.ActorId, record.Reason, record.CorrelationId);
        request.EntityLabel = record.EntityLabel ?? string.Empty;
        Fill(request.BeforeSummary, record.BeforeSummary);
        Fill(request.AfterSummary, record.AfterSummary);
        return SendAsync(request, record.Action, record.EntityId ?? Guid.Empty, ct);
    }

    private RecordAdminActionRequest NewRequest(
        string action, string entityType, Guid? entityId, Guid actorId, string reason, string correlationId) =>
        new()
        {
            SourceService = AdminAuditSources.AuthService,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString() ?? string.Empty,
            WorkspaceId = string.Empty,
            ActorId = actorId.ToString(),
            Reason = reason,
            Result = AdminAuditResults.Succeeded,
            PerformedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            CorrelationId = correlationId,
        };

    private async Task<Result> SendAsync(RecordAdminActionRequest request, string action, Guid entityId, CancellationToken ct)
    {
        AdminAuditRequestMetadata.FromHttpContext(_httpContextAccessor?.HttpContext).ApplyTo(request);

        try
        {
            var response = await _client.RecordAdminActionAsync(request, cancellationToken: ct);
            if (response.Recorded)
            {
                return Result.Success();
            }

            _logger.LogError(
                "Admin audit refused. Action: {Action}, Entity: {EntityId}, Reason: {Error}",
                action,
                entityId,
                response.ErrorMessage);
            return Result.Failure(
                "The action was not performed because it could not be recorded in the audit log.",
                ErrorCodes.InternalServerError);
        }
        catch (RpcException ex)
        {
            _logger.LogError(
                ex,
                "Admin audit call failed. Action: {Action}, Entity: {EntityId}, Status: {Status}",
                action,
                entityId,
                ex.Status);
            return Result.Failure(
                "The action was not performed because the audit log could not be reached.",
                ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// proto3 maps hold no nulls, so a null value is dropped rather than written as "".
    ///
    /// An empty string in a before/after summary would read as "this field was blank", which is a
    /// different claim from "this field was not part of the change".
    /// </summary>
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
