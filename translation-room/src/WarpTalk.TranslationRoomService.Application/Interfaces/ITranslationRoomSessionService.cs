using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Every method takes the caller's identity. It is not optional and it is not there for logging:
/// this whole surface previously had no authorization at all — neither the controller nor the
/// service ever asked who was calling — so any authenticated user could start, mutate or mark
/// ENDED a translation session on ANY room id they could guess or read off a URL.
///
/// Reads are gated on <c>RoomReadAccess.IsReadableBy</c> (host OR participant OR a standing
/// invitation), the same predicate the rooms list and the artifacts guard use; writes are gated on
/// <c>RoomHostAccess.HasHostAuthorityAsync</c> (room host OR workspace Owner/Admin), the same
/// predicate admission uses.
/// </summary>
public interface ITranslationRoomSessionService
{
    Task<Result<TranslationRoomSessionDto>> StartSessionAsync(Guid roomId, CreateTranslationRoomSessionDto dto, Guid requestedByUserId, CancellationToken ct = default);
    Task<Result<List<TranslationRoomSessionDto>>> GetSessionsAsync(Guid roomId, Guid requestedByUserId, string? requestedByEmail = null, CancellationToken ct = default);
    Task<Result<TranslationRoomSessionDto>> UpdateSessionAsync(Guid roomId, Guid sessionId, UpdateTranslationRoomSessionDto dto, Guid requestedByUserId, CancellationToken ct = default);
    Task<Result<TranslationRoomSessionDto>> EndSessionAsync(Guid roomId, Guid sessionId, Guid requestedByUserId, CancellationToken ct = default);
}
