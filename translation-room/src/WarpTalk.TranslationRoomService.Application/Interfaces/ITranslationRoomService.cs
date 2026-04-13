using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITranslationRoomService
{
    Task<Result<TranslationRoomDto>> CreateTranslationRoomAsync(CreateTranslationRoomRequest request, Guid hostId, CancellationToken ct = default);
    Task<Result<TranslationRoomDto>> GetTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default);
    Task<Result<TranslationRoomParticipantDto>> JoinTranslationRoomAsync(Guid translationRoomId, Guid userId, JoinTranslationRoomRequest request, CancellationToken ct = default);
    Task<Result> EndTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default);
}
