using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingChatService
{
    Task<Result<IEnumerable<MeetingChatMessageDto>>> GetRoomMessagesAsync(Guid roomId, Guid userId, CancellationToken ct = default);
    Task<Result<MeetingChatMessageDto>> SendMessageAsync(Guid roomId, Guid userId, SendMeetingChatMessageRequest request, CancellationToken ct = default);
    Task<Result<bool>> RequestTranslationAsync(Guid roomId, Guid messageId, Guid userId, TranslateMeetingChatMessageRequest request, CancellationToken ct = default);
    Task<Result<bool>> ModerateMessageAsync(Guid roomId, Guid messageId, Guid userId, ModerateMeetingChatMessageRequest request, CancellationToken ct = default);
}
