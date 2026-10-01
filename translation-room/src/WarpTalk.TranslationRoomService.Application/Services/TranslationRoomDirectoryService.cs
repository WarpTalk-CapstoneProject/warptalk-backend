using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Authorization;
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
    /// WT-699 / TC2402: the relay a reject is announced on. Optional so a caller that only reads
    /// (and the tests of the older writes) need not supply one; a missing relay only costs the
    /// rejected tab its notice, never the reject itself.
    /// </summary>
    private readonly IRedisStateRepository? _redisStateRepository;

    /// <summary>
    /// WT-704: resolves the room's generatable artifact languages for callers that opt in.
    /// Optional so existing test construction keeps compiling; DI always supplies it.
    /// </summary>
    private readonly IRoomArtifactLanguagePolicy? _artifactLanguagePolicy;

    private readonly ILogger<TranslationRoomDirectoryService>? _logger;

    /// <summary>
    /// WT-422: turns an invitation's email address into the invitee's name. Optional so existing
    /// test construction keeps compiling; DI always supplies it. Without it the people names carry
    /// everyone on the roster and simply no invitee who has not joined yet.
    /// </summary>
    private readonly IUserSettingsDirectory? _userSettingsDirectory;

    /// <summary>WT-422: how many names GetPeopleNamesAsync returns when the caller does not say.</summary>
    private const int DefaultMaxPeopleNames = 12;

    /// <summary>
    /// WT-422: the most it will ever return, whatever the caller asks. Also the bound on Auth
    /// lookups per call, which is the real cost — one GetUserByEmail per invitee.
    /// </summary>
    private const int MaxPeopleNames = 50;

    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";
    private const string ParticipantRejectedCommand = "ParticipantRejected";

    public TranslationRoomDirectoryService(
        ITranslationRoomRepository translationRoomRepository,
        ITranslationRoomParticipantRepository participantRepository,
        IUnitOfWork unitOfWork,
        IRedisStateRepository? redisStateRepository = null,
        IRoomArtifactLanguagePolicy? artifactLanguagePolicy = null,
        ILogger<TranslationRoomDirectoryService>? logger = null,
        IUserSettingsDirectory? userSettingsDirectory = null)
    {
        _translationRoomRepository = translationRoomRepository;
        _participantRepository = participantRepository;
        _unitOfWork = unitOfWork;
        _redisStateRepository = redisStateRepository;
        _artifactLanguagePolicy = artifactLanguagePolicy;
        _logger = logger;
        _userSettingsDirectory = userSettingsDirectory;
    }

    /// <inheritdoc />
    public Task<Result<TranslationRoomDto>> GetRoomAsync(
        Guid translationRoomId,
        CancellationToken ct = default)
        => GetRoomAsync(translationRoomId, includeArtifactLanguages: false, ct);

    /// <inheritdoc />
    public Task<Result<TranslationRoomDto>> GetRoomAsync(
        Guid translationRoomId,
        bool includeArtifactLanguages,
        CancellationToken ct = default)
        => GetRoomAsync(translationRoomId, includeArtifactLanguages, requesterEmail: null, ct);

    /// <inheritdoc />
    public async Task<Result<TranslationRoomDto>> GetRoomAsync(
        Guid translationRoomId,
        bool includeArtifactLanguages,
        string? requesterEmail,
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

        if (requesterEmail is not null)
            dto = dto with { IsRequesterInvited = await IsInvitedAsync(translationRoomId, requesterEmail, ct) };

        return Result.Success(dto);
    }

    /// <summary>
    /// WT-849: whether <paramref name="requesterEmail"/> holds a live invitation to this room —
    /// the same status allow-list and email normalisation
    /// <c>ArtifactAccessHelper.IsParticipantOrInvited</c> asks on the translation-room side, so a
    /// consumer across the mesh agrees with the in-process check instead of restating a looser
    /// copy of it.
    /// </summary>
    private async Task<bool> IsInvitedAsync(Guid translationRoomId, string requesterEmail, CancellationToken ct)
    {
        var email = RoomReadAccess.NormalizeEmail(requesterEmail);
        if (email is null) return false;

        var invitations = await _unitOfWork.TranslationRoomInvitationRepository.FindAsync(
            i => i.TranslationRoomId == translationRoomId, ct: ct);

        return invitations.Any(i =>
            RoomReadAccess.NormalizeEmail(i.Email) == email &&
            RoomReadAccess.InvitationStatusesGrantingRead.Contains(i.Status));
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
            _logger?.LogWarning(ex, "Could not resolve generatable artifact languages for room {RoomId}", room.Id);
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

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> GetPeopleNamesAsync(
        Guid translationRoomId,
        int maxNames,
        CancellationToken ct = default)
    {
        var limit = maxNames <= 0 ? DefaultMaxPeopleNames : Math.Min(maxNames, MaxPeopleNames);

        var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        if (room == null)
            return Result.Failure<IReadOnlyList<string>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        var participants = await _participantRepository.FindAsync(
            p => p.TranslationRoomId == translationRoomId, "", ct);

        var names = new List<string>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Roster first, in order of how certainly the person is part of this conversation. Ties
        // keep arrival order, so in a room too big for the budget the people who came first win.
        var roster = participants
            .Select(p => (Participant: p, Rank: PeopleNameRank(room, p)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Participant.JoinedAt ?? x.Participant.CreatedAt);
        foreach (var (participant, _) in roster)
        {
            if (TryAddPeopleName(names, seen, participant.DisplayName) && names.Count >= limit)
                return Result.Success<IReadOnlyList<string>>(names);
        }

        if (_userSettingsDirectory is null)
            return Result.Success<IReadOnlyList<string>>(names);

        // Then invitees. An invitation is only an email address until the person joins, so the
        // name comes from Auth. Someone already on the roster is usually invited too; their name
        // comes back identical and the de-duplication drops it, which costs one lookup, not a slot.
        var invitations = await _unitOfWork.TranslationRoomInvitationRepository.FindAsync(
            i => i.TranslationRoomId == translationRoomId && i.Status != "DECLINED", ct: ct);
        var emails = invitations
            .OrderBy(i => i.CreatedAt)
            .Select(i => i.Email?.Trim())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit - names.Count)
            .ToList();
        if (emails.Count == 0)
            return Result.Success<IReadOnlyList<string>>(names);

        var directory = _userSettingsDirectory;
        var invitees = await Task.WhenAll(emails.Select(async email =>
        {
            try
            {
                return await directory.GetDisplayNameByEmailAsync(email!, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The directory already swallows RpcException; this is the belt for anything else.
                // A name is never worth failing the call over.
                _logger?.LogWarning(ex, "Could not resolve an invitee's name for room {RoomId}", translationRoomId);
                return null;
            }
        }));

        foreach (var name in invitees)
        {
            if (TryAddPeopleName(names, seen, name) && names.Count >= limit)
                break;
        }

        return Result.Success<IReadOnlyList<string>>(names);
    }

    /// <summary>
    /// WT-422: where a roster row sits in the people names, or -1 to leave it out.
    /// <para>
    /// The host first, whatever their row says — they are seeded INVITED and promoted on arrival,
    /// and they are in the meeting either way. Then anyone admitted and still holding a seat, then
    /// anyone who was admitted and left.
    /// </para>
    /// <para>
    /// Left out: the bridge stand-in (its name is a label, not a person); KICKED and REJECTED (the
    /// host's decision that they are not part of this meeting); and WAITING and non-host INVITED,
    /// which on the roster only ever mean "knocked and was never admitted" (WT-563) — a stranger at
    /// the door is not someone the host asked for. Real invitees come in through the invitation
    /// table instead.
    /// </para>
    /// </summary>
    private static int PeopleNameRank(TranslationRoom room, TranslationRoomParticipant participant)
    {
        if (participant.UserId == TranslationRoomConstants.ExternalBridgeParticipantUserId)
            return -1;
        if (participant.UserId is { } userId && room.IsHostedBy(userId))
            return 0;

        return participant.Status switch
        {
            TranslationRoomParticipantStatuses.Connected => 1,
            TranslationRoomParticipantStatuses.Disconnected => 1,
            TranslationRoomParticipantStatuses.Left => 2,
            _ => -1,
        };
    }

    /// <summary>
    /// WT-422: adds one name if it is a real, new one. Whitespace is collapsed the way the STT
    /// worker collapses it, so two spellings that differ only in spacing are one person.
    /// </summary>
    private static bool TryAddPeopleName(List<string> names, HashSet<string> seen, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var cleaned = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.Equals(cleaned, TranslationRoomConstants.HostDisplayNameFallback, StringComparison.OrdinalIgnoreCase)
            || string.Equals(cleaned, TranslationRoomConstants.ExternalBridgeDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!seen.Add(cleaned))
            return false;

        names.Add(cleaned);
        return true;
    }

    public async Task<Result<int>> CountActiveRoomsByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        var count = await _translationRoomRepository.CountActiveByWorkspaceAsync(workspaceId, ct);
        return Result.Success(count);
    }

    /// <inheritdoc />
    public async Task<Result<RosterRemovalOutcome>> KickParticipantByUserAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid participantUserId,
        CancellationToken ct = default)
    {
        var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        if (room == null)
            return Result.Failure<RosterRemovalOutcome>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        // Re-checked here rather than trusted from MeetingService, for the same reason
        // TransferHostAsync re-checks it: host authority is READ out of this service's tables on
        // every join and every host-gated operation, so this service is the one that has to agree
        // a kick was legitimate.
        if (!room.IsHostedBy(requestedByUserId))
            return Result.Failure<RosterRemovalOutcome>(TranslationRoomConstants.ErrorOnlyHostCanKick, ErrorCodes.Forbidden);

        if (room.IsHostedBy(participantUserId))
            return Result.Failure<RosterRemovalOutcome>(TranslationRoomConstants.ErrorCannotKickHost, ErrorCodes.ValidationError);

        var participant = await _participantRepository.GetByRoomAndUserAsync(
            translationRoomId, participantUserId, ct);

        // Nothing to terminate. Not an error: MeetingService evicted somebody this service never
        // recorded, and reporting a failure would make the host retry a kick that already worked.
        if (participant == null)
            return Result.Success(RosterRemovalOutcome.NotOnRoster);

        // Idempotent — the host can press kick twice, and MeetingService retries. WT-699 / TC2103:
        // idempotent is not the same as "it happened again", so the answer says which.
        if (participant.Status == TranslationRoomParticipantStatuses.Kicked)
            return Result.Success(RosterRemovalOutcome.AlreadyRemoved);

        participant.Status = TranslationRoomParticipantStatuses.Kicked;
        participant.UpdatedAt = DateTime.UtcNow;
        _participantRepository.Update(participant);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(RosterRemovalOutcome.Removed);
    }

    /// <inheritdoc />
    public async Task<Result<RosterRemovalOutcome>> RejectParticipantByUserAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid participantUserId,
        CancellationToken ct = default)
    {
        var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        if (room == null)
            return Result.Failure<RosterRemovalOutcome>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        // The same re-check the kick makes: MeetingService's own host check is the fast path, and
        // this service — which owns the lobby row — is the one that has to agree.
        if (!room.IsHostedBy(requestedByUserId))
            return Result.Failure<RosterRemovalOutcome>(TranslationRoomConstants.ErrorOnlyHostCanReject, ErrorCodes.Forbidden);

        var participant = await _participantRepository.GetByRoomAndUserAsync(
            translationRoomId, participantUserId, ct);

        if (participant == null)
            return Result.Failure<RosterRemovalOutcome>(TranslationRoomConstants.ErrorParticipantNotWaiting, ErrorCodes.ValidationError);

        if (participant.Status == TranslationRoomParticipantStatuses.Rejected)
            return Result.Success(RosterRemovalOutcome.AlreadyRemoved);

        // WAITING is the knock. INVITED is a knock whose tab dropped before the host answered —
        // MarkParticipantDisconnectedAsync moves WAITING there — and refusing it is the same act.
        // Anything else has already been let in (or thrown out), and "reject" is the wrong verb.
        if (participant.Status is not (TranslationRoomParticipantStatuses.Waiting
            or TranslationRoomParticipantStatuses.Invited))
        {
            return Result.Failure<RosterRemovalOutcome>(TranslationRoomConstants.ErrorParticipantNotWaiting, ErrorCodes.ValidationError);
        }

        participant.Status = TranslationRoomParticipantStatuses.Rejected;
        participant.UpdatedAt = DateTime.UtcNow;
        _participantRepository.Update(participant);
        await _unitOfWork.SaveChangesAsync(ct);

        await PublishParticipantRejectedAsync(translationRoomId, participantUserId);

        return Result.Success(RosterRemovalOutcome.Removed);
    }

    /// <summary>
    /// Tell the person's lobby tab. Published after the save, and never throws: the row is already
    /// REJECTED, which is what refuses their next join, so a lost notice costs them only the
    /// sentence explaining why — failing the host's action here would be strictly worse.
    /// </summary>
    private async Task PublishParticipantRejectedAsync(Guid translationRoomId, Guid rejectedUserId)
    {
        if (_redisStateRepository is null)
        {
            return;
        }

        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                Command = ParticipantRejectedCommand,
                RoomId = translationRoomId.ToString(),
                UserId = rejectedUserId.ToString()
            });

            await _redisStateRepository.PublishAsync(GatewayCommandsChannel, payload);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Failed to publish ParticipantRejected for RoomId: {RoomId}, UserId: {UserId}. The participant is REJECTED "
                + "and cannot join, but their lobby tab will not be told until it retries.",
                translationRoomId,
                rejectedUserId);
        }
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
