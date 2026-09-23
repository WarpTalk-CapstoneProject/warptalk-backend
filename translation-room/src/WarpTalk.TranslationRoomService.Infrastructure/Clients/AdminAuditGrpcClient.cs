using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Clients;

/// <summary>
/// Writes this service's admin actions into the platform audit log over gRPC — the same contract
/// auth uses (<c>AdminAuditGrpcClient</c> there). Every failure comes back as a failed
/// <see cref="Result"/>; nothing is swallowed, because the caller abandons its change when the
/// record fails.
/// </summary>
public sealed class AdminAuditGrpcClient : IAdminAuditRecorder
{
    private readonly AdminAuditService.AdminAuditServiceClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdminAuditGrpcClient> _logger;

    public AdminAuditGrpcClient(
        AdminAuditService.AdminAuditServiceClient client,
        ILogger<AdminAuditGrpcClient> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result> RecordAsync(
        string action,
        string entityType,
        Guid actorId,
        string reason,
        string correlationId,
        IReadOnlyDictionary<string, string?>? beforeSummary = null,
        IReadOnlyDictionary<string, string?>? afterSummary = null,
        CancellationToken ct = default)
    {
        var request = new RecordAdminActionRequest
        {
            SourceService = AdminAuditSources.TranslationRoomService,
            Action = action,
            EntityType = entityType,
            // A catalog row is keyed by its code, not a GUID, and the store's entity id is a GUID.
            // Left empty; the code travels in the before/after summaries instead.
            EntityId = string.Empty,
            // Platform-wide reference data: no workspace. Empty rather than plausible.
            WorkspaceId = string.Empty,
            ActorId = actorId.ToString(),
            Reason = reason,
            Result = AdminAuditResults.Succeeded,
            PerformedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            CorrelationId = correlationId,
        };

        Fill(request.BeforeSummary, beforeSummary);
        Fill(request.AfterSummary, afterSummary);

        try
        {
            var response = await _client.RecordAdminActionAsync(request, cancellationToken: ct);
            if (response.Recorded)
            {
                return Result.Success();
            }

            _logger.LogError(
                "Admin audit refused. Action: {Action}, Reason: {Error}", action, response.ErrorMessage);
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
