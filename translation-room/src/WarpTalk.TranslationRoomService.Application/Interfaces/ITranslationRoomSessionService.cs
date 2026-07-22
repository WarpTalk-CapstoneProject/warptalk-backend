using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITranslationRoomSessionService
{
    Task<Result<TranslationRoomSessionDto>> StartSessionAsync(Guid roomId, CreateTranslationRoomSessionDto dto, CancellationToken ct = default);
    Task<Result<List<TranslationRoomSessionDto>>> GetSessionsAsync(Guid roomId, CancellationToken ct = default);
    Task<Result<TranslationRoomSessionDto>> UpdateSessionAsync(Guid roomId, Guid sessionId, UpdateTranslationRoomSessionDto dto, CancellationToken ct = default);
    Task<Result<TranslationRoomSessionDto>> EndSessionAsync(Guid roomId, Guid sessionId, CancellationToken ct = default);
}
