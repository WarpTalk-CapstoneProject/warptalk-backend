using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Mappers;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Enums;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Services;

public class MeetingHistoryService : IMeetingHistoryService
{
    private readonly IUnitOfWork _unitOfWork;

    public MeetingHistoryService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<MeetingHistoryResponse>> GetMeetingHistoryAsync(Guid userId, GetMeetingHistoryRequest request, CancellationToken ct = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 50);

        // Build base query: rooms where user is creator or participant
        var query = _unitOfWork.MeetingRoomRepository.Query()
            .Where(r => r.CreatedBy == userId ||
                         r.MeetingParticipants.Any(p => p.UserId == userId));

        // Apply status filter
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var statuses = request.Status
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .ToList();
            query = query.Where(r => statuses.Contains(r.Status.ToUpper()));
        }

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchTerm = request.Search.Trim().ToLower();
            query = query.Where(r => r.ProviderRoomName.ToLower().Contains(searchTerm));
        }

        // Apply date range filter
        if (request.From.HasValue)
            query = query.Where(r => r.CreatedAt >= request.From.Value);

        if (request.To.HasValue)
            query = query.Where(r => r.CreatedAt <= request.To.Value);

        var total = query.Count();

        var roomEntities = query
            .OrderByDescending(r => r.EndedAt ?? r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var roomIds = roomEntities.Select(r => r.Id).ToList();

        // Batch load participants
        var participantsByRoom = _unitOfWork.MeetingParticipantRepository.Query()
            .Where(p => roomIds.Contains(p.MeetingRoomId))
            .ToList()
            .GroupBy(p => p.MeetingRoomId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.ToParticipantDto()).ToList());

        // Batch load chat message counts
        var chatCountsByRoom = _unitOfWork.MeetingChatMessageRepository.Query()
            .Where(m => roomIds.Contains(m.MeetingRoomId))
            .GroupBy(m => m.MeetingRoomId)
            .Select(g => new { RoomId = g.Key, Count = g.Count() })
            .ToDictionary(x => x.RoomId, x => x.Count);

        // Batch load recent messages (last 5 per room)
        var recentMessagesByRoom = _unitOfWork.MeetingChatMessageRepository.Query()
            .Where(m => roomIds.Contains(m.MeetingRoomId) && !m.IsHidden)
            .OrderByDescending(m => m.CreatedAt)
            .ToList()
            .GroupBy(m => m.MeetingRoomId)
            .ToDictionary(
                g => g.Key,
                g => g.Take(5).Reverse().Select(m => m.ToDto()).ToList()
            );

        var items = roomEntities.Select(room =>
        {
            var participantCount = participantsByRoom.GetValueOrDefault(room.Id)?.Count ?? 0;
            var chatCount = chatCountsByRoom.GetValueOrDefault(room.Id, 0);

            return new MeetingHistoryItemDto
            {
                Room = room.ToRoomDto(userId, participantCount, chatCount),
                Participants = participantsByRoom.GetValueOrDefault(room.Id, new List<MeetingParticipantDto>()),
                RecentMessages = recentMessagesByRoom.GetValueOrDefault(room.Id, new List<MeetingChatMessageDto>())
            };
        }).ToList();

        return Result.Success(new MeetingHistoryResponse(items, total, page, pageSize));
    }

    public async Task<Result<MeetingRoomDetailDto>> GetMeetingRoomDetailAsync(Guid roomId, Guid userId, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.GetByIdAsync(roomId, ct);
        if (room == null)
            return Result.Failure<MeetingRoomDetailDto>("Room not found.", "NOT_FOUND");

        // Check access: must be creator or participant
        var isParticipant = await _unitOfWork.MeetingParticipantRepository
            .AnyAsync(p => p.MeetingRoomId == roomId && p.UserId == userId, ct);

        if (room.CreatedBy != userId && !isParticipant)
            return Result.Failure<MeetingRoomDetailDto>("Not authorized to view this room.", "FORBIDDEN");

        var participants = await _unitOfWork.MeetingParticipantRepository
            .FindAsync(p => p.MeetingRoomId == roomId, ct: ct);

        var messages = await _unitOfWork.MeetingChatMessageRepository
            .FindAsync(m => m.MeetingRoomId == roomId && !m.IsHidden, ct: ct);

        var totalChatMessages = messages.Count();

        var detail = new MeetingRoomDetailDto
        {
            Room = room.ToRoomDto(userId, participants.Count, totalChatMessages),
            Participants = participants.Select(p => p.ToParticipantDto()).ToList(),
            TotalChatMessages = totalChatMessages,
            RecentMessages = messages.OrderBy(m => m.CreatedAt).Select(m => m.ToDto()).ToList()
        };

        return Result.Success(detail);
    }
}
