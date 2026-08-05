using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomDirectoryService : ITranslationRoomDirectoryService
{
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _participantRepository;

    public TranslationRoomDirectoryService(
        ITranslationRoomRepository translationRoomRepository,
        ITranslationRoomParticipantRepository participantRepository)
    {
        _translationRoomRepository = translationRoomRepository;
        _participantRepository = participantRepository;
    }

    public async Task<Result<IReadOnlyList<TranslationRoomParticipantSummaryDto>>> GetParticipantsAsync(
        Guid translationRoomId,
        CancellationToken ct = default)
    {
        var participants = await _participantRepository.FindAsync(
            p => p.TranslationRoomId == translationRoomId, "", ct);

        var summaries = participants
            .Select(p => new TranslationRoomParticipantSummaryDto(
                p.UserId,
                p.DisplayName ?? string.Empty,
                p.Role ?? string.Empty,
                p.SpeakLanguage ?? string.Empty,
                p.Status ?? string.Empty))
            .ToList();

        return Result.Success<IReadOnlyList<TranslationRoomParticipantSummaryDto>>(summaries);
    }

    public async Task<Result<int>> CountActiveRoomsByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        var count = await _translationRoomRepository.CountActiveByWorkspaceAsync(workspaceId, ct);
        return Result.Success(count);
    }
}
