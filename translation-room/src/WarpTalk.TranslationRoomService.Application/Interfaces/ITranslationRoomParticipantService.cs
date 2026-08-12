using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITranslationRoomParticipantService
{
    Task<Result<List<TranslationRoomParticipantDto>>> GetParticipantsAsync(Guid translationRoomId, GetParticipantsRequest request, Guid requestedByUserId, string? requestedByEmail = null, CancellationToken ct = default);
    Task<Result> UpdateParticipantAudioAsync(Guid translationRoomId, Guid participantId, UpdateParticipantAudioRequest request, Guid requestedByUserId, CancellationToken ct = default);
    Task<Result> AdmitParticipantAsync(Guid translationRoomId, Guid participantId, Guid requestedByUserId, CancellationToken ct = default);
    Task<Result> KickParticipantAsync(Guid translationRoomId, Guid participantId, Guid requestedByUserId, CancellationToken ct = default);
    Task<Result> LeaveRoomAsync(Guid translationRoomId, Guid requestedByUserId, CancellationToken ct = default);

    /// <summary>
    /// WT-354: the participant's socket dropped. This is NOT the same event as leaving, and the
    /// two must not share <see cref="LeaveRoomAsync"/> — LEFT is terminal and the roster hides it,
    /// so a backgrounded tab or a network blip erased someone who was still in the call.
    /// </summary>
    Task<Result> MarkParticipantDisconnectedAsync(Guid translationRoomId, Guid requestedByUserId, CancellationToken ct = default);
}
