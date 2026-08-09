using System;

namespace WarpTalk.MeetingService.Application.DTOs;

public record ActiveMeetingDto(
    Guid TranslationRoomId,
    string ProviderRoomName,
    string Status,
    int MaxQuota,
    int UsedToken,
    DateTime CreatedAt
);

public class AdjustQuotaRequest
{
    public int AdditionalQuota { get; set; }
}
