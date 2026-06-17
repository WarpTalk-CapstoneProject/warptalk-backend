using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Application.DTOs;

// --- Response DTOs ---

public class MeetingRoomDto
{
    public Guid Id { get; set; }
    public Guid TranslationRoomId { get; set; }
    public string ProviderRoomName { get; set; } = null!;
    public string Status { get; set; } = null!;
    public bool IsActive { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? DurationSeconds { get; set; }
    public int ParticipantCount { get; set; }
    public int ChatMessageCount { get; set; }
    public bool IsHost { get; set; }
}

public class MeetingParticipantDto
{
    public Guid Id { get; set; }
    public Guid MeetingRoomId { get; set; }
    public Guid? UserId { get; set; }
    public string ProviderIdentity { get; set; } = null!;
    public bool IsActive { get; set; }
    public DateTime? JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
}

public class MeetingHistoryItemDto
{
    public MeetingRoomDto Room { get; set; } = null!;
    public List<MeetingParticipantDto> Participants { get; set; } = new();
    public List<MeetingChatMessageDto> RecentMessages { get; set; } = new();
}

public class MeetingHistoryResponse
{
    public List<MeetingHistoryItemDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }

    public MeetingHistoryResponse(List<MeetingHistoryItemDto> items, int totalCount, int page, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        Page = page;
        PageSize = pageSize;
    }
}

public class MeetingRoomDetailDto
{
    public MeetingRoomDto Room { get; set; } = null!;
    public List<MeetingParticipantDto> Participants { get; set; } = new();
    public int TotalChatMessages { get; set; }
    public List<MeetingChatMessageDto> RecentMessages { get; set; } = new();
}

// --- Request DTOs ---

public class GetMeetingHistoryRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Status { get; set; }
    public string? Search { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}
