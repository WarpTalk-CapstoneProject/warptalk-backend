using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.EventHandlers;

/// <summary>
/// WT-419 — the half of a mid-meeting language change that reaches the audio mesh.
///
/// THE BUG THIS CLOSES
///     TranslationRoomHub.SetSpeakLanguage and SetListenLanguage wrote the new language to a Redis
///     hash, broadcast it to the other clients, and stopped. GenerateRoutesAsync reads
///     participant.SpeakLanguage / participant.ListenLanguage — Postgres columns — and only runs at
///     StartTranslationRoomAsync or when somebody joins.
///
///     So the mesh was pinned to whatever languages a pair held at join time. Two people who joined
///     speaking the same language had NO route at all (IsTranslationRequired is false for a matched
///     pair), and nothing existed afterwards to create one. Production, 15 Aug: one participant on
///     en/en spoke English, the other on vi/vi received neither dubbed audio nor a translated
///     transcript, because the pair had no route and never would.
///
///     STT made it worse by being right: _language_hint_for_stt reads the Redis hash, so speech was
///     transcribed in the correct NEW language and then had nowhere to go. One meeting, two layers,
///     two different beliefs about what language somebody speaks.
///
/// WHY IT ENDS IN GenerateRoutesAsync AND NOT A TARGETED EDIT
///     A language change can require any of three outcomes per pair — create a route that never
///     existed, restate an existing one, or delete one that is no longer needed because the pair now
///     match. GenerateRoutesAsync already does all three (its `isRouteStale` branch was written for
///     exactly this and had no caller), publishes the mesh to the AI workers, and is the same code
///     path join and start use. Re-deriving a subset here is how the two would drift.
/// </summary>
public class ParticipantLanguageProcessor : IParticipantLanguageProcessor
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomAudioRouteService _audioRouteService;
    private readonly ILogger<ParticipantLanguageProcessor> _logger;

    public ParticipantLanguageProcessor(
        IUnitOfWork unitOfWork,
        ITranslationRoomAudioRouteService audioRouteService,
        ILogger<ParticipantLanguageProcessor> logger)
    {
        _unitOfWork = unitOfWork;
        _audioRouteService = audioRouteService;
        _logger = logger;
    }

    public async Task<Result> ProcessLanguageChangeAsync(
        Guid roomId,
        Guid userId,
        string? speakLanguage,
        string? listenLanguage,
        CancellationToken ct = default)
    {
        try
        {
            var participants = await _unitOfWork.TranslationRoomParticipantRepository
                .FindAsync(p => p.TranslationRoomId == roomId && p.UserId == userId, "", ct);

            // A guest has no UserId, so this legitimately finds nobody. Rejoining also leaves older
            // rows behind, so the live one is the row to move — not the first one the query returns.
            var participant = participants
                .Where(p => p.LeftAt == null)
                .OrderByDescending(p => p.JoinedAt ?? p.CreatedAt)
                .FirstOrDefault();

            if (participant == null)
            {
                // Not an error worth retrying or sending to the DLQ: the person may have left
                // between the hub call and this consumer reading it.
                _logger.LogInformation(
                    "Language change for user {UserId} in room {RoomId} matched no active participant; nothing to apply",
                    userId, roomId);
                return Result.Success();
            }

            var normalizedSpeak = Normalize(speakLanguage);
            var normalizedListen = Normalize(listenLanguage);

            // Null means "this hub call did not carry that language", not "blank the column".
            // SetSpeakLanguage and SetListenLanguage are separate calls and each publishes one field.
            var changed = false;
            if (normalizedSpeak != null && !string.Equals(participant.SpeakLanguage, normalizedSpeak, StringComparison.OrdinalIgnoreCase))
            {
                participant.SpeakLanguage = normalizedSpeak;
                changed = true;
            }
            if (normalizedListen != null && !string.Equals(participant.ListenLanguage, normalizedListen, StringComparison.OrdinalIgnoreCase))
            {
                participant.ListenLanguage = normalizedListen;
                changed = true;
            }

            if (!changed)
            {
                // The client re-sends its language on reconnect and on every render that reconciles
                // it, so this is the common case. Regenerating the mesh anyway would republish
                // routes to every AI worker for nothing.
                return Result.Success();
            }

            participant.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.TranslationRoomParticipantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            // Saved BEFORE regenerating, because GenerateRoutesAsync reads these columns back out
            // of the repository. Regenerating first would rebuild the mesh from the old languages
            // and look, from the outside, exactly like the bug being fixed.
            var regenerated = await _audioRouteService.GenerateRoutesAsync(roomId, ct);
            if (!regenerated.IsSuccess)
            {
                _logger.LogError(
                    "Persisted the language change for user {UserId} in room {RoomId} but could not regenerate routes: {Error}",
                    userId, roomId, regenerated.Error);
                return Result.Failure(regenerated.Error ?? AudioRouteConstants.ErrorUnexpected, regenerated.ErrorCode);
            }

            _logger.LogInformation(
                "Applied language change for user {UserId} in room {RoomId} (speak={Speak}, listen={Listen}) and regenerated {RouteCount} routes",
                userId, roomId, participant.SpeakLanguage, participant.ListenLanguage, regenerated.Value?.Count ?? 0);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying language change for user {UserId} in room {RoomId}", userId, roomId);
            return Result.Failure(AudioRouteConstants.ErrorInternalProcessingEvent, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>Mirrors the gateway's NormalizeLanguageCode; blank and the "auto" sentinel are not choices.</summary>
    private static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var normalized = LanguageHelper.NormalizeLanguageCode(language);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // "auto" reaches STT as a free-run hint and is never a language a route can target.
            return null;
        }
        return normalized;
    }
}
