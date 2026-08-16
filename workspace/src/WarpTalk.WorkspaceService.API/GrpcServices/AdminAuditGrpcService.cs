using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.GrpcServices;

/// <summary>
/// The append path into the platform audit log, for a service that cannot publish onto the bus.
///
/// This is the same store and the same <see cref="IAdminAuditLogService.RecordAsync"/> the
/// admin.action_recorded consumer writes through — including its de-duplication on
/// (source, correlation, action, entity), which is what makes a caller's retry after a timeout
/// safe rather than a second entry for one action.
///
/// It answers rather than throws when the record is refused. The caller is expected to abandon
/// its own uncommitted change on a false, and a refusal it can read is more useful there than an
/// RpcException it has to classify. Malformed input is still an RpcException: that is the caller
/// being wrong, not the store.
/// </summary>
public sealed class AdminAuditGrpcService : AdminAuditService.AdminAuditServiceBase
{
    private readonly IAdminAuditLogService _auditLog;

    public AdminAuditGrpcService(IAdminAuditLogService auditLog)
    {
        _auditLog = auditLog;
    }

    public override async Task<RecordAdminActionResponse> RecordAdminAction(
        RecordAdminActionRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.SourceService)
            || string.IsNullOrWhiteSpace(request.Action)
            || string.IsNullOrWhiteSpace(request.EntityType))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "source_service, action and entity_type are required."));
        }

        if (!Guid.TryParse(request.ActorId, out var actorId))
        {
            // The actor is the whole point of an audit entry. An unparseable one would produce a
            // record of an action nobody took.
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "actor_id must be a GUID."));
        }

        var result = await _auditLog.RecordAsync(
            new AdminActionRecordedEvent(
                request.SourceService,
                request.Action,
                request.EntityType,
                ParseOptionalGuid(request.EntityId),
                ParseOptionalGuid(request.WorkspaceId),
                actorId,
                request.Reason,
                string.IsNullOrWhiteSpace(request.Result) ? AdminAuditResults.Succeeded : request.Result,
                ParsePerformedAt(request.PerformedAt),
                string.IsNullOrWhiteSpace(request.CorrelationId) ? null : request.CorrelationId,
                ToSummary(request.BeforeSummary),
                ToSummary(request.AfterSummary)),
            context.CancellationToken);

        return new RecordAdminActionResponse
        {
            Recorded = result.IsSuccess,
            ErrorMessage = result.IsSuccess ? string.Empty : result.Error ?? "The action could not be recorded.",
        };
    }

    /// <summary>proto3 has no null, so an absent id arrives as "". Anything unparseable is absent too.</summary>
    private static Guid? ParseOptionalGuid(string value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>
    /// Blank means "now", which <see cref="IAdminAuditLogService.RecordAsync"/> supplies from its
    /// own clock. Returning default rather than DateTime.UtcNow here keeps one clock in the story.
    /// </summary>
    private static DateTime ParsePerformedAt(string value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : default;

    private static IReadOnlyDictionary<string, string?>? ToSummary(
        Google.Protobuf.Collections.MapField<string, string> map) =>
        map.Count == 0 ? null : map.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
}
