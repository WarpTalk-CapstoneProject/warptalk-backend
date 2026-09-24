using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Authorization;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Authorization;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc />
public class MeetingActionItemService : IMeetingActionItemService
{
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal)
    {
        MeetingActionItemConstants.StatusOpen,
        MeetingActionItemConstants.StatusDone,
        MeetingActionItemConstants.StatusDropped
    };

    /// <summary>
    /// The same type approval sends, so a task WarpBot gave somebody reads exactly like one the
    /// minutes gave them — and needs no new entry in NotificationValidator.Schemas.
    /// </summary>
    private const string ActionItemAssignedNotificationType = "ACTION_ITEM_ASSIGNED";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMemberDirectory _workspaceMemberDirectory;
    private readonly ILogger<MeetingActionItemService> _logger;
    /// <summary>Nullable, matching MeetingMinutesService: a deployment without it still runs.</summary>
    private readonly WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? _notificationClient;
    private readonly string _frontendBaseUrl;

    public MeetingActionItemService(
        IUnitOfWork unitOfWork,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        ILogger<MeetingActionItemService> logger,
        WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? notificationClient = null,
        IOptions<AppSettings>? appSettings = null)
    {
        _unitOfWork = unitOfWork;
        _workspaceMemberDirectory = workspaceMemberDirectory;
        _logger = logger;
        _notificationClient = notificationClient;
        _frontendBaseUrl = appSettings?.Value.FrontendBaseUrl?.TrimEnd('/') ?? "http://localhost:3000";
    }

    public async Task<Result<List<MeetingActionItemDto>>> GetForRoomAsync(
        Guid roomId, Guid userId, string? userEmail, CancellationToken ct = default)
    {
        var readable = await _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(room => room.Id == roomId && room.DeletedAt == null && room.IsActive)
            .AnyAsync(RoomReadAccess.IsReadableBy(userId, userEmail), ct);

        if (!readable)
        {
            // NotFound rather than Forbidden, so this endpoint cannot be used to discover that a
            // room exists.
            return Result.Failure<List<MeetingActionItemDto>>(
                MeetingMinutesConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
        }

        var items = await _unitOfWork.MeetingActionItemRepository.GetByRoomIdAsync(roomId, ct);
        return Result.Success(items.Select(item => ToDto(item, null)).ToList());
    }

    public async Task<Result<List<MeetingActionItemDto>>> GetMineAsync(
        Guid workspaceId, Guid userId, string? status, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(status) && !Statuses.Contains(status))
        {
            return Result.Failure<List<MeetingActionItemDto>>(
                MeetingActionItemConstants.ErrorInvalidStatus, ErrorCodes.ValidationError);
        }

        var items = await _unitOfWork.MeetingActionItemRepository
            .GetForAssigneeAsync(workspaceId, userId, status, ct);

        // The room title is what makes a cross-meeting list readable — "write the release note"
        // means nothing without the meeting it came from. Fetched once per distinct room rather
        // than per item.
        var titles = await LoadRoomTitlesAsync(items.Select(item => item.TranslationRoomId), ct);

        return Result.Success(items
            .Select(item => ToDto(item, titles.GetValueOrDefault(item.TranslationRoomId)))
            .ToList());
    }

    public async Task<Result<MeetingActionItemDto>> CreateAsync(
        Guid roomId,
        Guid userId,
        string? userEmail,
        CreateActionItemRequest request,
        CancellationToken ct = default)
    {
        var task = request?.Task?.Trim() ?? string.Empty;
        if (task.Length == 0)
        {
            return Result.Failure<MeetingActionItemDto>(
                MeetingActionItemConstants.ErrorTaskRequired, ErrorCodes.ValidationError);
        }

        if (task.Length > MeetingActionItemConstants.MaxTaskLength)
        {
            return Result.Failure<MeetingActionItemDto>(
                MeetingActionItemConstants.ErrorTaskTooLong, ErrorCodes.ValidationError);
        }

        var ownerName = request!.OwnerName?.Trim();
        if (ownerName is { Length: > MeetingActionItemConstants.MaxOwnerNameLength })
        {
            return Result.Failure<MeetingActionItemDto>(
                MeetingActionItemConstants.ErrorOwnerNameTooLong, ErrorCodes.ValidationError);
        }

        // The same gate as reading the room's action items: whoever can see a meeting's tasks may
        // add one to it. NotFound rather than Forbidden, so this cannot probe for rooms either.
        var room = await _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(candidate => candidate.Id == roomId && candidate.DeletedAt == null && candidate.IsActive)
            .Where(RoomReadAccess.IsReadableBy(userId, userEmail))
            .FirstOrDefaultAsync(ct);

        if (room == null)
        {
            return Result.Failure<MeetingActionItemDto>(
                MeetingMinutesConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
        }

        var participants = await _unitOfWork.TranslationRoomParticipantRepository
            .GetByRoomIdAsync(roomId, ct) ?? new List<TranslationRoomParticipant>();

        TranslationRoomParticipant? ownerParticipant = null;
        Guid? assigneeUserId = null;
        string? recordedOwnerName = null;

        if (request.AssignToSelf)
        {
            // The caller is the one owner that needs no resolving. Their participant row, when
            // they have one, supplies the name the room knows them by; a host reading the room
            // later sees "Tú", not an id.
            assigneeUserId = userId;
            ownerParticipant = participants
                .Where(participant => participant.UserId == userId)
                .OrderByDescending(participant => participant.JoinedAt ?? participant.CreatedAt)
                .FirstOrDefault();
            recordedOwnerName = ownerParticipant?.DisplayName ?? (string.IsNullOrWhiteSpace(ownerName) ? null : ownerName);
        }
        else if (!string.IsNullOrWhiteSpace(ownerName))
        {
            ownerParticipant = ActionItemOwnerResolver.Resolve(ownerName, participants);
            if (ownerParticipant == null)
            {
                // Refused, not stored unassigned. Approval keeps an unresolved name because the
                // meeting is over and nobody can be asked; here the person asking is still in the
                // conversation, and "who did you mean?" is one question away.
                return Result.Failure<MeetingActionItemDto>(
                    MeetingActionItemConstants.ErrorOwnerUnresolved, ErrorCodes.ValidationError);
            }

            assigneeUserId = ownerParticipant.UserId;
            recordedOwnerName = ownerName;
        }

        var now = DateTime.UtcNow;
        var item = new MeetingActionItem
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = room.Id,
            WorkspaceId = room.WorkspaceId,
            SourceMinutesId = null,
            Source = MeetingActionItemConstants.SourceAssistant,
            CreatedBy = userId,
            SeriesId = room.SeriesId,
            Task = task,
            OwnerName = recordedOwnerName,
            OwnerParticipantId = ownerParticipant?.Id,
            AssigneeUserId = assigneeUserId,
            // No citation: nothing was said at a known moment of a recording, it was asked for.
            AtMs = null,
            Status = MeetingActionItemConstants.StatusOpen,
            DueDate = request.DueDate,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _unitOfWork.MeetingActionItemRepository.AddAsync(item, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "action_item_created: ItemId={ItemId} RoomId={RoomId} Source={Source} Assigned={Assigned} SelfAssigned={SelfAssigned}",
            item.Id, room.Id, item.Source, item.AssigneeUserId.HasValue, request.AssignToSelf);

        // Nobody is told about a task they gave themselves.
        if (item.AssigneeUserId.HasValue && item.AssigneeUserId.Value != userId)
        {
            await NotifyAssigneeAsync(room, item, ct);
        }

        return Result.Success(ToDto(item, room.Title));
    }

    /// <summary>
    /// Same payload approval sends, field for field — NotificationValidator declares exactly these
    /// four and rejects the whole message on an undeclared one. Failures are logged and swallowed:
    /// the task is committed, and a lost notification must not turn it into an error.
    /// </summary>
    private async Task NotifyAssigneeAsync(TranslationRoom room, MeetingActionItem item, CancellationToken ct)
    {
        if (_notificationClient is null)
        {
            _logger.LogInformation(
                "action_item_notification_skipped: reason=client_unavailable RoomId={RoomId}", room.Id);
            return;
        }

        try
        {
            var title = room.Title ?? "Meeting";
            var request = new WarpTalk.Shared.Protos.SendNotificationRequest
            {
                UserId = item.AssigneeUserId!.Value.ToString(),
                Type = ActionItemAssignedNotificationType,
                Title = $"You were given a task in \"{title}\"",
                Body = item.Task,
                ActionUrl = $"{_frontendBaseUrl}/room/{room.Id}"
            };
            request.Metadata.Add("room_id", room.Id.ToString());
            request.Metadata.Add("room_title", title);
            request.Metadata.Add("action_item_id", item.Id.ToString());
            request.Metadata.Add("task", item.Task);

            await _notificationClient.SendNotificationAsync(request, cancellationToken: ct);

            _logger.LogInformation(
                "action_item_notification_sent: RoomId={RoomId} UserId={UserId} ItemId={ItemId}",
                room.Id, item.AssigneeUserId, item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to notify {UserId} about action item {ItemId}. The task itself is unaffected.",
                item.AssigneeUserId, item.Id);
        }
    }

    public async Task<Result<MeetingActionItemDto>> UpdateStatusAsync(
        Guid itemId, Guid userId, string status, DateOnly? dueDate, CancellationToken ct = default)
    {
        if (!Statuses.Contains(status))
        {
            return Result.Failure<MeetingActionItemDto>(
                MeetingActionItemConstants.ErrorInvalidStatus, ErrorCodes.ValidationError);
        }

        var item = await _unitOfWork.MeetingActionItemRepository.GetByIdAsync(itemId, ct);
        if (item == null)
        {
            return Result.Failure<MeetingActionItemDto>(
                MeetingActionItemConstants.ErrorActionItemNotFound, ErrorCodes.NotFound);
        }

        var room = await _unitOfWork.TranslationRoomRepository.GetByIdAsync(item.TranslationRoomId, ct);
        if (room == null || room.DeletedAt != null)
        {
            return Result.Failure<MeetingActionItemDto>(
                MeetingMinutesConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
        }

        var isAssignee = item.AssigneeUserId.HasValue && item.AssigneeUserId.Value == userId;
        var isHost = await RoomHostAccess.HasHostAuthorityAsync(
            room, userId, _workspaceMemberDirectory, ct);

        if (!isAssignee && !isHost)
        {
            // Reading a meeting is not licence to tick off somebody else's work.
            return Result.Failure<MeetingActionItemDto>(
                MeetingActionItemConstants.ErrorUnauthorizedClose, ErrorCodes.Forbidden);
        }

        var now = DateTime.UtcNow;
        item.Status = status;
        item.DueDate = dueDate ?? item.DueDate;

        // Reopening clears the closure rather than leaving a "closed at" on an open task.
        var isClosed = status != MeetingActionItemConstants.StatusOpen;
        item.ClosedAt = isClosed ? now : null;
        item.ClosedBy = isClosed ? userId : null;
        item.UpdatedAt = now;

        _unitOfWork.MeetingActionItemRepository.Update(item);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Action item {ItemId} in room {RoomId} moved to {Status}",
            item.Id, item.TranslationRoomId, status);

        return Result.Success(ToDto(item, room.Title));
    }

    private async Task<Dictionary<Guid, string>> LoadRoomTitlesAsync(
        IEnumerable<Guid> roomIds, CancellationToken ct)
    {
        var ids = roomIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        return await _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(room => ids.Contains(room.Id))
            .ToDictionaryAsync(room => room.Id, room => room.Title, ct);
    }

    private static MeetingActionItemDto ToDto(MeetingActionItem item, string? roomTitle) => new(
        item.Id,
        item.TranslationRoomId,
        roomTitle,
        item.SourceMinutesId,
        item.Task,
        item.OwnerName,
        item.OwnerParticipantId,
        item.AssigneeUserId,
        item.AtMs,
        item.Status,
        item.DueDate,
        item.ClosedAt,
        item.CreatedAt,
        item.Source);
}
