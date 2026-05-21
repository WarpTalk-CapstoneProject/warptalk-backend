using System;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface ITranslationRoomGrpcService
{
    Task<Result<Shared.Protos.GetTranslationRoomResponse>> GetRoomDetailsAsync(Guid translationRoomId);
    Task<Result<Shared.Protos.GetParticipantsByRoomIdResponse>> GetParticipantsAsync(Guid translationRoomId);
}
