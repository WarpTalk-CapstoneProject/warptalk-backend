using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

/// <inheritdoc cref="IAdminWorkspaceActionService"/>
public sealed class AdminWorkspaceActionService : IAdminWorkspaceActionService
{
    public const int MaxReasonLength = 500;
    public const int MaxNoticeTitleLength = 120;
    public const int MaxNoticeMessageLength = 2000;
    public const int MaxNoteLength = 4000;
    public const int DefaultTimelineLimit = 100;
    public const int MaxTimelineLimit = 500;

    /// <summary>Registered in the notification service's NotificationValidator.Schemas.</summary>
    public const string WorkspaceAdminNoticeNotificationType = "WORKSPACE_ADMIN_NOTICE";

    /// <summary>workspace_admin_actions.correlation_id is varchar(100).</summary>
    private const int MaxCorrelationLength = 100;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMemberService _memberService;
    private readonly IAdminWorkspaceService _adminWorkspaceService;
    private readonly IAdminAuditLogRepository _auditLog;
    private readonly IWorkspaceAdminNoteRepository _notes;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ILogger<AdminWorkspaceActionService> _logger;
    private readonly TimeProvider _time;
    private readonly WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? _notificationClient;

    public AdminWorkspaceActionService(
        IUnitOfWork unitOfWork,
        IWorkspaceMemberService memberService,
        IAdminWorkspaceService adminWorkspaceService,
        IAdminAuditLogRepository auditLog,
        IWorkspaceAdminNoteRepository notes,
        IAuthIdentityClient authIdentity,
        ILogger<AdminWorkspaceActionService> logger,
        TimeProvider? timeProvider = null,
        WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? notificationClient = null)
    {
        _unitOfWork = unitOfWork;
        _memberService = memberService;
        _adminWorkspaceService = adminWorkspaceService;
        _auditLog = auditLog;
        _notes = notes;
        _authIdentity = authIdentity;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _notificationClient = notificationClient;
    }

    public async Task<Result<AdminWorkspaceDetailDto>> TransferOwnershipAsync(
        Guid workspaceId, AdminTransferOwnershipRequest request, Guid actorId, string? correlationId, CancellationToken ct = default)
    {
        if (request is null || request.NewOwnerUserId == Guid.Empty)
            return Result.Failure<AdminWorkspaceDetailDto>("Choose the member who will own the workspace.", ErrorCodes.ValidationError);

        var transferred = await _memberService.AdminTransferOwnershipAsync(
            workspaceId, request.NewOwnerUserId, actorId, request.Reason, correlationId, ct);
        if (!transferred.IsSuccess)
            return Result.Failure<AdminWorkspaceDetailDto>(transferred.Error!, NormalizeCode(transferred.ErrorCode));

        return await _adminWorkspaceService.GetDetailAsync(workspaceId, ct);
    }

    /// <summary>
    /// Recorded FIRST and committed, then sent. A notice is irreversible once delivered, so the only
    /// ordering that never leaves a delivered notice unrecorded is this one; a send that then fails
    /// is followed by a <c>failed</c> entry, and the admin is told nothing went out.
    /// </summary>
    public async Task<Result<AdminWorkspaceNoticeResultDto>> SendNoticeAsync(
        Guid workspaceId, AdminWorkspaceNoticeRequest request, Guid actorId, string? correlationId, CancellationToken ct = default)
    {
        if (ValidateReason(request?.Reason) is { } reasonError)
            return Result.Failure<AdminWorkspaceNoticeResultDto>(reasonError, ErrorCodes.ValidationError);

        var title = request!.Title?.Trim() ?? string.Empty;
        var message = request.Message?.Trim() ?? string.Empty;
        if (title.Length == 0 || title.Length > MaxNoticeTitleLength)
            return Result.Failure<AdminWorkspaceNoticeResultDto>(
                $"The notice needs a title of at most {MaxNoticeTitleLength} characters.", ErrorCodes.ValidationError);
        if (message.Length == 0 || message.Length > MaxNoticeMessageLength)
            return Result.Failure<AdminWorkspaceNoticeResultDto>(
                $"The notice needs a message of at most {MaxNoticeMessageLength} characters.", ErrorCodes.ValidationError);

        if (_notificationClient is null)
            return Result.Failure<AdminWorkspaceNoticeResultDto>(
                "The notification service is not configured, so no notice was sent.", ErrorCodes.InternalServerError);

        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace is null)
            return Result.Failure<AdminWorkspaceNoticeResultDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
        if (workspace.DeletedAt is not null)
            return Result.Failure<AdminWorkspaceNoticeResultDto>(WorkspaceAdminErrors.DeletedWorkspaceIsImmutable, ErrorCodes.Conflict);

        var now = _time.GetUtcNow().UtcDateTime;
        var reason = request.Reason.Trim();
        var summary = new Dictionary<string, string?>
        {
            ["recipient_id"] = workspace.OwnerId.ToString(),
            ["title"] = title,
        };

        await _auditLog.AppendAsync(
            NewEntry(workspaceId, AdminAuditEntityTypes.Workspace, workspaceId, AdminAuditWorkspaceActions.NoticeSent,
                reason, actorId, now, correlationId, AdminAuditResults.Succeeded, null, summary),
            ct);
        await _unitOfWork.SaveChangesAsync(ct);

        try
        {
            var workspaceName = string.IsNullOrWhiteSpace(workspace.Name) ? "your workspace" : workspace.Name;
            var notification = new WarpTalk.Shared.Protos.SendNotificationRequest
            {
                UserId = workspace.OwnerId.ToString(),
                Type = WorkspaceAdminNoticeNotificationType,
                Title = title,
                Body = message,
                ActionUrl = workspace.Slug is { Length: > 0 } slug ? $"/{slug}" : "/workspace",
            };
            notification.Metadata.Add("workspace_id", workspace.Id.ToString());
            notification.Metadata.Add("workspace_name", workspaceName);

            var response = await _notificationClient.SendNotificationAsync(notification, cancellationToken: ct);
            if (!response.Success)
                throw new InvalidOperationException("The notification service refused the notice.");

            return Result.Success(new AdminWorkspaceNoticeResultDto(workspace.OwnerId, response.NotificationId, now));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin notice to the owner of workspace {WorkspaceId} was not delivered.", workspaceId);
            await _auditLog.AppendAsync(
                NewEntry(workspaceId, AdminAuditEntityTypes.Workspace, workspaceId, AdminAuditWorkspaceActions.NoticeSent,
                    reason, actorId, _time.GetUtcNow().UtcDateTime, FailedCorrelation(correlationId), AdminAuditResults.Failed, null, summary),
                ct);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Failure<AdminWorkspaceNoticeResultDto>(
                "The notice could not be delivered, so the owner received nothing. It is recorded as failed; try again.",
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminWorkspaceNoteDto>> AddNoteAsync(
        Guid workspaceId, AdminAddWorkspaceNoteRequest request, Guid actorId, string? correlationId, CancellationToken ct = default)
    {
        var body = request?.Body?.Trim() ?? string.Empty;
        if (body.Length == 0)
            return Result.Failure<AdminWorkspaceNoteDto>("A note cannot be empty.", ErrorCodes.ValidationError);
        if (body.Length > MaxNoteLength)
            return Result.Failure<AdminWorkspaceNoteDto>($"A note must be at most {MaxNoteLength} characters.", ErrorCodes.ValidationError);

        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace is null)
            return Result.Failure<AdminWorkspaceNoteDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);

        // Deleted workspaces take notes too: "why was this deleted, and who asked" is exactly the
        // kind of thing written down after the fact.
        var now = _time.GetUtcNow().UtcDateTime;
        var note = new WorkspaceAdminNote
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Body = body,
            AuthorId = actorId,
            CreatedAt = now,
        };

        await _notes.AppendAsync(note, ct);
        // The audit reason for a note is its own text, capped to the reason width the log keeps.
        await _auditLog.AppendAsync(
            NewEntry(workspaceId, AdminAuditEntityTypes.WorkspaceNote, note.Id, AdminAuditWorkspaceActions.NoteAdded,
                body.Length > MaxReasonLength ? body[..MaxReasonLength] : body, actorId, now, correlationId,
                AdminAuditResults.Succeeded, null, null),
            ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var author = await _authIdentity.GetUserByIdAsync(actorId, ct);
        return Result.Success(new AdminWorkspaceNoteDto(note.Id, note.Body, actorId, author?.FullName, note.CreatedAt));
    }

    public async Task<Result<IReadOnlyList<AdminWorkspaceTimelineEntryDto>>> GetTimelineAsync(
        Guid workspaceId, int? limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? DefaultTimelineLimit, 1, MaxTimelineLimit);

        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace is null)
            return Result.Failure<IReadOnlyList<AdminWorkspaceTimelineEntryDto>>(
                WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);

        return Result.Success(await BuildTimelineAsync(workspaceId, take, ct));
    }

    public async Task<Result<AdminWorkspaceExportDto>> ExportAsync(
        Guid workspaceId, AdminWorkspaceExportRequest request, Guid actorId, string? correlationId, CancellationToken ct = default)
    {
        if (ValidateReason(request?.Reason) is { } reasonError)
            return Result.Failure<AdminWorkspaceExportDto>(reasonError, ErrorCodes.ValidationError);

        var detail = await _adminWorkspaceService.GetDetailAsync(workspaceId, ct);
        if (!detail.IsSuccess)
            return Result.Failure<AdminWorkspaceExportDto>(detail.Error!, detail.ErrorCode);

        var members = await _adminWorkspaceService.GetMembersAsync(workspaceId, ct);
        if (!members.IsSuccess)
            return Result.Failure<AdminWorkspaceExportDto>(members.Error!, members.ErrorCode);

        var timeline = await BuildTimelineAsync(workspaceId, MaxTimelineLimit, ct);
        var now = _time.GetUtcNow().UtcDateTime;

        // Recorded before the file is handed over: an export that could not be recorded is not
        // returned.
        await _auditLog.AppendAsync(
            NewEntry(workspaceId, AdminAuditEntityTypes.Workspace, workspaceId, AdminAuditWorkspaceActions.DataExported,
                request!.Reason.Trim(), actorId, now, correlationId, AdminAuditResults.Succeeded, null,
                new Dictionary<string, string?>
                {
                    ["members"] = members.Value!.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["timeline_entries"] = timeline.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
            ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new AdminWorkspaceExportDto(now, actorId, detail.Value!, members.Value!, timeline));
    }

    private async Task<IReadOnlyList<AdminWorkspaceTimelineEntryDto>> BuildTimelineAsync(Guid workspaceId, int take, CancellationToken ct)
    {
        // Every audit row stamped with this workspace, from any service — lifecycle here, credits
        // and plan from billing, sign-outs from auth — plus the notes.
        var (actions, _) = await _auditLog.QueryAsync(
            new AdminAuditLogFilter(1, take, null, null, null, null, workspaceId, null, null, null, null),
            ct);
        var notes = await _notes.GetForWorkspaceAsync(workspaceId, take, ct);

        var actorIds = actions.Select(a => a.PerformedBy).Concat(notes.Select(n => n.AuthorId)).Distinct().ToList();
        var names = new Dictionary<Guid, string?>();
        foreach (var id in actorIds)
        {
            var user = await _authIdentity.GetUserByIdAsync(id, ct);
            names[id] = user?.FullName;
        }

        return actions
            .Select(a => new AdminWorkspaceTimelineEntryDto(
                "action",
                a.Id,
                a.PerformedAt,
                a.Action,
                a.SourceService,
                a.EntityType,
                a.EntityId,
                a.Result,
                a.Reason,
                a.PerformedBy,
                names.GetValueOrDefault(a.PerformedBy),
                ParseSummary(a.BeforeSummary),
                ParseSummary(a.AfterSummary)))
            .Concat(notes.Select(n => new AdminWorkspaceTimelineEntryDto(
                "note", n.Id, n.CreatedAt, null, null, null, null, null, n.Body, n.AuthorId,
                names.GetValueOrDefault(n.AuthorId), null, null)))
            .OrderByDescending(entry => entry.At)
            .ThenByDescending(entry => entry.Id)
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// The stored summaries are jsonb objects; a value that is not a string (a number written by
    /// another producer) is shown as its JSON text rather than dropped.
    /// </summary>
    public static IReadOnlyDictionary<string, string?>? ParseSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText(),
                });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WorkspaceAdminAction NewEntry(
        Guid workspaceId,
        string entityType,
        Guid entityId,
        string action,
        string reason,
        Guid actorId,
        DateTime at,
        string? correlationId,
        string result,
        IReadOnlyDictionary<string, string?>? before,
        IReadOnlyDictionary<string, string?>? after) => new()
        {
            Id = Guid.NewGuid(),
            SourceService = AdminAuditSources.WorkspaceService,
            WorkspaceId = workspaceId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Reason = reason,
            Result = result,
            PerformedBy = actorId,
            PerformedAt = at,
            CorrelationId = correlationId,
            BeforeSummary = before is null ? null : JsonSerializer.Serialize(before),
            AfterSummary = after is null ? null : JsonSerializer.Serialize(after),
        };

    /// <summary>
    /// The failed entry needs its own correlation id — the store de-duplicates on (source,
    /// correlation, action, entity) and would otherwise drop it as a repeat of the first.
    /// </summary>
    public static string? FailedCorrelation(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return null;
        const string suffix = ":failed";
        var head = correlationId.Length + suffix.Length > MaxCorrelationLength
            ? correlationId[..(MaxCorrelationLength - suffix.Length)]
            : correlationId;
        return head + suffix;
    }

    private static string? ValidateReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return WorkspaceAdminErrors.ReasonRequired;
        if (trimmed.Length > MaxReasonLength) return WorkspaceAdminErrors.ReasonTooLong;
        return null;
    }

    private static string NormalizeCode(string? code) => code ?? ErrorCodes.InternalServerError;
}
