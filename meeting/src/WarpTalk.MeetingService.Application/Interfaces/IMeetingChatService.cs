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
    Task<Result<MeetingChatMessageDto>> SendMessageAsync(Guid roomId, Guid userId, SendMeetingChatMessageRequest request, string? bearerToken = null, CancellationToken ct = default);
    Task<Result<MeetingChatTranslationDto>> RequestTranslationAsync(Guid roomId, Guid messageId, Guid userId, TranslateMeetingChatMessageRequest request, CancellationToken ct = default);
    Task<Result<bool>> ModerateMessageAsync(Guid roomId, Guid messageId, Guid userId, ModerateMeetingChatMessageRequest request, CancellationToken ct = default);
    Task<Result<MeetingChatMessageDto>> UploadFileAsync(Guid roomId, Guid userId, UploadMeetingChatFileRequest request, CancellationToken ct = default);
    Task<Result<MeetingChatFileDownloadResult>> DownloadFileAsync(Guid roomId, Guid messageId, Guid userId, CancellationToken ct = default);
}
