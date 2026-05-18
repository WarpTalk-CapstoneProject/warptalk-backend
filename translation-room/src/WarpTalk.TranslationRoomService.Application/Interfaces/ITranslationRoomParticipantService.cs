using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITranslationRoomParticipantService
{
    Task<Result<List<TranslationRoomParticipantDto>>> GetParticipantsAsync(Guid translationRoomId, GetParticipantsRequest request, Guid requestedByUserId, CancellationToken ct = default);
    Task<Result> UpdateParticipantAudioAsync(Guid translationRoomId, Guid participantId, UpdateParticipantAudioRequest request, Guid requestedByUserId, CancellationToken ct = default);
    Task<Result> AdmitParticipantAsync(Guid translationRoomId, Guid participantId, Guid requestedByUserId, CancellationToken ct = default);
    Task<Result> KickParticipantAsync(Guid translationRoomId, Guid participantId, Guid requestedByUserId, CancellationToken ct = default);
    Task<Result> LeaveRoomAsync(Guid translationRoomId, Guid requestedByUserId, CancellationToken ct = default);
}
