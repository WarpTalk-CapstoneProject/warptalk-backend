using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomDirectoryService : ITranslationRoomDirectoryService
{
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _participantRepository;

    /// <summary>WT-359: this interface acquired its first write, and a write needs a commit.</summary>
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>
    /// WT-704: resolves the room's generatable artifact languages for callers that opt in.
    /// Optional so existing test construction keeps compiling; DI always supplies it.
    /// </summary>
    private readonly IRoomArtifactLanguagePolicy? _artifactLanguagePolicy;

    private readonly ILogger<TranslationRoomDirectoryService> _logger;

    public TranslationRoomDirectoryService(
        ITranslationRoomRepository translationRoomRepository,
        ITranslationRoomParticipantRepository participantRepository,
        IUnitOfWork unitOfWork,
        IRoomArtifactLanguagePolicy? artifactLanguagePolicy = null,
        ILogger<TranslationRoomDirectoryService>? logger = null)
    {
        _translationRoomRepository = translationRoomRepository;
        _participantRepository = participantRepository;
        _unitOfWork = unitOfWork;
        _artifactLanguagePolicy = artifactLanguagePolicy;
        _logger = logger ?? NullLogger<TranslationRoomDirectoryService>.Instance;
    }

    /// <inheritdoc />
    public Task<Result<TranslationRoomDto>> GetRoomAsync(
        Guid translationRoomId,
        CancellationToken ct = default)
        => GetRoomAsync(translationRoomId, includeArtifactLanguages: false, ct);

    /// <inheritdoc />
    public async Task<Result<TranslationRoomDto>> GetRoomAsync(
        Guid translationRoomId,
        bool includeArtifactLanguages,
        CancellationToken ct = default)
    {
        var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

        if (room == null)
            return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        // Byte-for-byte the body ITranslationRoomService.GetTranslationRoomAsync had before WT-334
        // added its guard, so the mesh sees no behaviour change at all — including the seat count,
        // which GetTranslationRoomById does not read today but which keeps the two DTOs identical
        // rather than subtly divergent.
        var dto = room.ToResponseDto(
            await _participantRepository.CountSeatHoldingParticipantsAsync(room.Id, ct),
            await _participantRepository.CountEverJoinedAsync(room.Id, ct));

        if (includeArtifactLanguages)
            dto = dto with { ArtifactLanguages = await ResolveArtifactLanguagesAsync(room, ct) };

        return Result.Success(dto);
    }

    /// <summary>
    /// WT-704: the generatable artifact languages, or <c>null</c> when they could not be computed.
    ///
    /// Mirrors <c>TranslationRoomService.ResolveArtifactLanguagesAsync</c> except that it does not
    /// gate on a terminal status — the caller opted in, so it needs the answer whatever state the
    /// room is in. A failure degrades to <c>null</c> ("not resolved") rather than failing the read:
    /// the caller then falls back to the room's own declared set, which never widens past L2.
    /// </summary>
    private async Task<RoomArtifactLanguagesDto?> ResolveArtifactLanguagesAsync(
        TranslationRoom room,
        CancellationToken ct)
    {
        if (_artifactLanguagePolicy is null)
            return null;

        try
        {
            return new RoomArtifactLanguagesDto(
                await _artifactLanguagePolicy.GetGeneratableLanguagesAsync(room, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not resolve generatable artifact languages for room {RoomId}", room.Id);
            return null;
        }
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

    /// <inheritdoc />
    public async Task<Result<bool>> KickParticipantByUserAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid participantUserId,
        CancellationToken ct = default)
    {
        var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        if (room == null)
            return Result.Failure<bool>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        // Re-checked here rather than trusted from MeetingService, for the same reason
        // TransferHostAsync re-checks it: host authority is READ out of this service's tables on
        // every join and every host-gated operation, so this service is the one that has to agree
        // a kick was legitimate.
        if (!room.IsHostedBy(requestedByUserId))
            return Result.Failure<bool>(TranslationRoomConstants.ErrorOnlyHostCanKick, ErrorCodes.Forbidden);

        if (room.IsHostedBy(participantUserId))
            return Result.Failure<bool>(TranslationRoomConstants.ErrorCannotKickHost, ErrorCodes.ValidationError);

        var participant = await _participantRepository.GetByRoomAndUserAsync(
            translationRoomId, participantUserId, ct);

        // Nothing to terminate. Not an error: MeetingService evicted somebody this service never
        // recorded, and reporting a failure would make the host retry a kick that already worked.
        if (participant == null)
            return Result.Success(false);

        // Idempotent — the host can press kick twice, and MeetingService retries.
        if (participant.Status == TranslationRoomParticipantStatuses.Kicked)
            return Result.Success(true);

        participant.Status = TranslationRoomParticipantStatuses.Kicked;
        participant.UpdatedAt = DateTime.UtcNow;
        _participantRepository.Update(participant);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(true);
    }

    /// <inheritdoc />
    public async Task<Result<Guid>> TransferHostAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid newHostUserId,
        CancellationToken ct = default)
    {
        var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        if (room == null)
            return Result.Failure<Guid>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        // The effective host, and ONLY them. The booker is refused once they have handed the room
        // over — which is the behaviour WT-359 asks for in as many words: the outgoing host gets it
        // back if and only if the incoming host transfers it back. Allowing the booker here would
        // reinstate the bug through the front door.
        if (!room.IsHostedBy(requestedByUserId))
            return Result.Failure<Guid>(
                "Only the current host can transfer this room.", ErrorCodes.Forbidden);

        var previousHostId = room.EffectiveHostId;

        // Idempotent. MeetingService retries, and the Gateway's host-offline election can race a
        // deliberate transfer to the same person; neither should be an error, and neither should
        // record a handover from someone to themselves.
        if (previousHostId == newHostUserId)
            return Result.Success(previousHostId);

        var newHostParticipant = await _participantRepository.GetByRoomAndUserAsync(
            translationRoomId, newHostUserId, ct);

        // The roster is this service's own record of who is in the room, and host authority that
        // points at somebody with no participant row would be unreachable by every host-gated
        // operation here. MeetingService checks its own live-participant table before calling; this
        // is the same question asked of the table that will actually be read afterwards.
        if (newHostParticipant == null)
            return Result.Failure<Guid>(
                "The new host is not a participant of this room.", ErrorCodes.ValidationError);

        var now = DateTime.UtcNow;

        // Null when handing the room back to the booker, so the column keeps meaning "somebody
        // other than the booker is running this" rather than accumulating a no-op value.
        room.ActiveHostId = newHostUserId == room.HostId ? null : newHostUserId;
        room.UpdatedAt = now;
        _translationRoomRepository.Update(room);

        newHostParticipant.Role = nameof(TranslationRoomParticipantRole.HOST);
        newHostParticipant.UpdatedAt = now;
        _participantRepository.Update(newHostParticipant);

        // Demote the outgoing host. Their row may legitimately be absent — the host can transfer
        // on their way out, and HandleHostOfflineAsync elects a successor for someone who has
        // already gone — so a missing row is not a failure, it is the normal departing case.
        var previousHostParticipant = await _participantRepository.GetByRoomAndUserAsync(
            translationRoomId, previousHostId, ct);
        if (previousHostParticipant != null)
        {
            previousHostParticipant.Role = nameof(TranslationRoomParticipantRole.PARTICIPANT);
            previousHostParticipant.UpdatedAt = now;
            _participantRepository.Update(previousHostParticipant);
        }

        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(previousHostId);
    }
}
