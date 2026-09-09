using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Authorization;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Authorization;
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

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMemberDirectory _workspaceMemberDirectory;
    private readonly ILogger<MeetingActionItemService> _logger;

    public MeetingActionItemService(
        IUnitOfWork unitOfWork,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        ILogger<MeetingActionItemService> logger)
    {
        _unitOfWork = unitOfWork;
        _workspaceMemberDirectory = workspaceMemberDirectory;
        _logger = logger;
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
        item.CreatedAt);
}
