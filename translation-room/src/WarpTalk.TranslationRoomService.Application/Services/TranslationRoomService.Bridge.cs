using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <summary>
/// One shared EXTERNAL_BRIDGE room per Google Meet code, with a single audio capturer.
///
/// Every WarpTalk user with the desktop app in the same Meet call (same workspace) claims the
/// call's code and lands in the SAME room, as their own participant with their own mic track, so
/// speakers are attributed exactly as in a native meeting. Exactly one desktop — the capturer —
/// publishes the far side's mixed audio as the stand-in identity; two would double the far side.
///
/// The capturer holds a lease (<see cref="BridgeRoomConstants.CapturerStaleAfter"/>) renewed by
/// heartbeat. When the capturer leaves, nothing ends: the lease simply goes stale and the next
/// claim or an explicit takeover hands the job to another participant. The reapers count people,
/// not the capturer (RoomPresence), so a room with a live member is never ended because the
/// capturer left.
///
/// Cross-workspace joining is out of scope: claim only ever finds rooms in the caller's workspace,
/// and a second workspace in the same call gets its own room.
/// </summary>
public partial class TranslationRoomService
{
    private static int LeaseSeconds => (int)BridgeRoomConstants.CapturerStaleAfter.TotalSeconds;

    private static int HeartbeatSeconds => (int)BridgeRoomConstants.CapturerHeartbeatInterval.TotalSeconds;

    /// <inheritdoc />
    public async Task<Result<ClaimBridgeRoomResponse>> ClaimBridgeRoomAsync(
        ClaimBridgeRoomRequest request,
        Guid userId,
        string? userEmail = null,
        CancellationToken ct = default)
    {
        try
        {
            var workspaceId = request.WorkspaceId ?? Guid.Empty;
            if (workspaceId == Guid.Empty)
                return Result.Failure<ClaimBridgeRoomResponse>(
                    ApiMessageConstants.ValidationMessages.WorkspaceRequired, ErrorCodes.ValidationError);

            if (!GoogleMeetCode.TryNormalize(request.MeetCode, out var meetCode))
                return Result.Failure<ClaimBridgeRoomResponse>(
                    BridgeRoomConstants.ErrorInvalidMeetCode, ErrorCodes.ValidationError);

            if (request.AudioMode is not null && BridgeRoomConstants.NormalizeAudioMode(request.AudioMode) is null)
                return Result.Failure<ClaimBridgeRoomResponse>(
                    BridgeRoomConstants.ErrorInvalidAudioMode, ErrorCodes.ValidationError);

            // Membership first, so a stranger cannot even learn whether a room exists for a code.
            // Members may JOIN without the create permission; creating still asks the workspace
            // (ValidateMeetingCreationAsync inside the create path), exactly as POST /translation-rooms.
            if (!await _workspaceMemberDirectory.IsMemberAsync(workspaceId, userId, ct))
                return Result.Failure<ClaimBridgeRoomResponse>(
                    BridgeRoomConstants.ErrorNotWorkspaceMember, ErrorCodes.Forbidden);

            var room = await _translationRoomRepository.GetOpenBridgeRoomByMeetCodeAsync(workspaceId, meetCode, ct);
            if (room is null)
            {
                var created = await CreateTranslationRoomCoreAsync(
                    BuildBridgeCreateRequest(request, workspaceId, meetCode),
                    userId,
                    ct,
                    occurrence: null,
                    bridgeMeetCode: meetCode);

                if (created.IsSuccess)
                {
                    // The creator is the host, seated CONNECTED at creation (bridge rule in
                    // BuildHostParticipant), and was stamped capturer in the same INSERT.
                    var hostRow = await _participantRepository.GetByRoomAndUserAsync(created.Value!.Id, userId, ct);
                    var hostMode = await ApplyClaimedAudioModeAsync(created.Value!.Id, hostRow, request.AudioMode, userId, ct);
                    return Result.Success(new ClaimBridgeRoomResponse(
                        created.Value!,
                        hostRow is null ? null : TranslationRoomParticipantMapper.ToDto(hostRow),
                        BridgeRoomConstants.RoleCapturer,
                        Created: true,
                        HeartbeatSeconds,
                        LeaseSeconds,
                        hostMode));
                }

                if (created.ErrorCode != ErrorCodes.Conflict)
                    return Result.Failure<ClaimBridgeRoomResponse>(created.Error!, created.ErrorCode);

                // Lost the race on the unique index: someone else's room is there now.
                room = await _translationRoomRepository.GetOpenBridgeRoomByMeetCodeAsync(workspaceId, meetCode, ct);
                if (room is null)
                    return Result.Failure<ClaimBridgeRoomResponse>(
                        "Could not claim a room for this Google Meet call. Try again.", ErrorCodes.Conflict);
            }

            return await JoinClaimedBridgeRoomAsync(room, request, userId, userEmail, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error claiming a bridge room. UserId: {UserId}, WorkspaceId: {WorkspaceId}", userId, request.WorkspaceId);
            return Result.Failure<ClaimBridgeRoomResponse>(
                "An unexpected error occurred while claiming the room.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Seats the caller through the ordinary join — same suspension, kick/reject, language, workspace
    /// whitelist and capacity rules — then offers them the capturer lease if it is free or stale.
    /// </summary>
    private async Task<Result<ClaimBridgeRoomResponse>> JoinClaimedBridgeRoomAsync(
        TranslationRoom room,
        ClaimBridgeRoomRequest request,
        Guid userId,
        string? userEmail,
        CancellationToken ct)
    {
        // In a bridge room a person hears the call in their OWN language (the host rule in
        // BuildHostParticipant): the far side and the other members are translated into it.
        var speak = string.IsNullOrWhiteSpace(request.SourceLanguage)
            ? (await _userSettingsDirectory.GetDefaultsAsync(userId, ct))?.DefaultSpeakLanguage
            : request.SourceLanguage;
        speak = string.IsNullOrWhiteSpace(speak) ? null : LanguageHelper.NormalizeLanguageCode(speak);

        var displayName = await ResolveBridgeDisplayNameAsync(request.DisplayName, userId, userEmail, ct);

        var joined = await JoinTranslationRoomAsync(
            new JoinTranslationRoomRequest(room.TranslationRoomCode, displayName, speak, speak),
            userId,
            userEmail,
            ct);
        if (!joined.IsSuccess)
            return Result.Failure<ClaimBridgeRoomResponse>(joined.Error!, joined.ErrorCode);

        var roomDto = joined.Value!.Room;
        var role = BridgeRoomConstants.RoleMember;

        // Only someone actually in the room may capture; a row still in a lobby is not.
        if (joined.Value.Participant.Status == TranslationRoomParticipantStatuses.Connected)
        {
            var now = _utcNow();
            if (await _translationRoomRepository.TryAcquireBridgeCapturerAsync(
                    room.Id, userId, now, now - BridgeRoomConstants.CapturerStaleAfter, ct))
            {
                role = BridgeRoomConstants.RoleCapturer;
                roomDto = roomDto with { BridgeCapturerUserId = userId };
            }
        }

        var participantDto = joined.Value.Participant;
        var row = await _participantRepository.GetByRoomAndUserAsync(room.Id, userId, ct);
        var audioMode = await ApplyClaimedAudioModeAsync(room.Id, row, request.AudioMode, userId, ct);
        if (row is not null)
            participantDto = participantDto with { IsBridgeTextOnly = row.IsBridgeTextOnly };

        return Result.Success(new ClaimBridgeRoomResponse(
            roomDto,
            participantDto,
            role,
            Created: false,
            HeartbeatSeconds,
            LeaseSeconds,
            audioMode));
    }

    /// <summary>
    /// The audio mode a claim asked for, applied to the caller's own row. Never fails the claim: a
    /// claim is also how a desktop REJOINS after a socket blip, and refusing it over the mode would
    /// strand the person outside the room. So a request the rules forbid (text → voice while
    /// translation runs) simply keeps the current mode, and the response says which one is in force.
    /// </summary>
    private async Task<string> ApplyClaimedAudioModeAsync(
        Guid roomId, TranslationRoomParticipant? row, string? requested, Guid userId, CancellationToken ct)
    {
        if (row is null)
            return BridgeRoomConstants.AudioModeVoice;

        var wanted = BridgeRoomConstants.NormalizeAudioMode(requested);
        if (wanted is null)
            return BridgeRoomConstants.AudioModeOf(row.IsBridgeTextOnly);

        var change = await DecideAudioModeChangeAsync(roomId, row, wanted, ct);
        if (change.Locked)
        {
            _logger.LogInformation(
                "Claim by {UserId} asked for voice in bridge room {RoomId}, but they are text-only and translation is running; keeping text.",
                userId, roomId);
        }
        else if (change.Changed)
        {
            await PersistAudioModeAsync(roomId, row, wanted == BridgeRoomConstants.AudioModeText, userId, ct);
        }

        return BridgeRoomConstants.AudioModeOf(row.IsBridgeTextOnly);
    }

    /// <inheritdoc />
    public async Task<Result<BridgeAudioModeDto>> SetBridgeAudioModeAsync(
        Guid translationRoomId,
        Guid userId,
        string? mode,
        CancellationToken ct = default)
    {
        var wanted = BridgeRoomConstants.NormalizeAudioMode(mode);
        if (wanted is null)
            return Result.Failure<BridgeAudioModeDto>(BridgeRoomConstants.ErrorInvalidAudioMode, ErrorCodes.ValidationError);

        var refusal = await LoadOpenBridgeRoomAsync(translationRoomId, ct);
        if (refusal is not null)
            return Result.Failure<BridgeAudioModeDto>(refusal.Value.Error, refusal.Value.Code);

        // Only the person themselves: the mode describes the hardware on THEIR desk. Same seat rule
        // as takeover — in the room, or just was on a socket blip.
        var row = await _participantRepository.GetByRoomAndUserAsync(translationRoomId, userId, ct);
        if (row?.Status is not (TranslationRoomParticipantStatuses.Connected or TranslationRoomParticipantStatuses.Disconnected)
            || row.UserId == TranslationRoomConstants.ExternalBridgeParticipantUserId)
            return Result.Failure<BridgeAudioModeDto>(BridgeRoomConstants.ErrorNotParticipantForAudioMode, ErrorCodes.Forbidden);

        var change = await DecideAudioModeChangeAsync(translationRoomId, row, wanted, ct);
        if (change.Locked)
            return Result.Failure<BridgeAudioModeDto>(
                BridgeRoomConstants.ErrorAudioModeLocked, BridgeRoomConstants.ErrorCodeAudioModeLocked);

        if (change.Changed)
            await PersistAudioModeAsync(translationRoomId, row, wanted == BridgeRoomConstants.AudioModeText, userId, ct);

        return Result.Success(new BridgeAudioModeDto(
            translationRoomId, userId, BridgeRoomConstants.AudioModeOf(row.IsBridgeTextOnly), change.TranslationActive));
    }

    /// <summary>
    /// Whether moving <paramref name="row"/> to <paramref name="wanted"/> changes anything, and
    /// whether the rules forbid it. Text → voice is locked only while a translation session is
    /// active: before Start the choice is still free, and Stop reopens it.
    /// </summary>
    private async Task<(bool Changed, bool Locked, bool TranslationActive)> DecideAudioModeChangeAsync(
        Guid roomId, TranslationRoomParticipant row, string wanted, CancellationToken ct)
    {
        var wantText = wanted == BridgeRoomConstants.AudioModeText;
        var translationActive =
            await _translationRoomSessionRepository.GetActiveSessionByRoomIdAsync(roomId, ct) is not null;

        if (wantText == row.IsBridgeTextOnly)
            return (false, false, translationActive);

        var locked = !wantText && translationActive;
        return (!locked, locked, translationActive);
    }

    /// <summary>
    /// Writes the mode and republishes the room's routes, so tts_worker stops (or resumes)
    /// synthesizing this person's outbound dub on the next sentence instead of the next route event.
    /// A failed republish is logged, not returned: the column is the source of truth and the next
    /// publish of any kind carries it.
    /// </summary>
    private async Task PersistAudioModeAsync(
        Guid roomId, TranslationRoomParticipant row, bool textOnly, Guid userId, CancellationToken ct)
    {
        row.IsBridgeTextOnly = textOnly;
        row.UpdatedAt = _utcNow();
        _participantRepository.Update(row);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} set bridge audio mode {Mode} in room {RoomId}",
            userId, BridgeRoomConstants.AudioModeOf(textOnly), roomId);

        try
        {
            // RefreshDubVoiceAsync is the existing "republish this room's routes" entry point (it
            // writes nothing; PublishRoutesUpdateAsync re-derives every route's TextOnly flag).
            var republished = await _audioRouteService.RefreshDubVoiceAsync(roomId, userId, ct);
            if (!republished.IsSuccess)
                _logger.LogWarning(
                    "Republishing routes after a bridge audio mode change failed for room {RoomId}: {Error}",
                    roomId, republished.Error);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Republishing routes after a bridge audio mode change threw for room {RoomId}", roomId);
        }
    }

    /// <inheritdoc />
    public async Task<Result<BridgeCapturerStatusDto>> HeartbeatBridgeCapturerAsync(
        Guid translationRoomId,
        Guid userId,
        CancellationToken ct = default)
    {
        var refusal = await LoadOpenBridgeRoomAsync(translationRoomId, ct);
        if (refusal is not null)
            return Result.Failure<BridgeCapturerStatusDto>(refusal.Value.Error, refusal.Value.Code);

        var now = _utcNow();
        if (!await _translationRoomRepository.TryRenewBridgeCapturerAsync(translationRoomId, userId, now, ct))
            return Result.Failure<BridgeCapturerStatusDto>(BridgeRoomConstants.ErrorNotCapturer, ErrorCodes.Conflict);

        return Result.Success(new BridgeCapturerStatusDto(
            translationRoomId, BridgeRoomConstants.RoleCapturer, userId, now, LeaseSeconds));
    }

    /// <inheritdoc />
    public async Task<Result<BridgeCapturerStatusDto>> TakeOverBridgeCapturerAsync(
        Guid translationRoomId,
        Guid userId,
        CancellationToken ct = default)
    {
        var refusal = await LoadOpenBridgeRoomAsync(translationRoomId, ct);
        if (refusal is not null)
            return Result.Failure<BridgeCapturerStatusDto>(refusal.Value.Error, refusal.Value.Code);

        // A participant who is (or just was, on a socket blip) in the room. Not a lobby row, and not
        // somebody who left, was kicked or was refused.
        var participant = await _participantRepository.GetByRoomAndUserAsync(translationRoomId, userId, ct);
        if (participant?.Status is not (TranslationRoomParticipantStatuses.Connected or TranslationRoomParticipantStatuses.Disconnected))
            return Result.Failure<BridgeCapturerStatusDto>(BridgeRoomConstants.ErrorNotParticipant, ErrorCodes.Forbidden);

        var now = _utcNow();
        if (!await _translationRoomRepository.TryAcquireBridgeCapturerAsync(
                translationRoomId, userId, now, now - BridgeRoomConstants.CapturerStaleAfter, ct))
            return Result.Failure<BridgeCapturerStatusDto>(BridgeRoomConstants.ErrorCapturerStillLive, ErrorCodes.Conflict);

        _logger.LogInformation("User {UserId} is now the bridge capturer of room {RoomId}", userId, translationRoomId);
        return Result.Success(new BridgeCapturerStatusDto(
            translationRoomId, BridgeRoomConstants.RoleCapturer, userId, now, LeaseSeconds));
    }

    /// <summary>
    /// Seats the join capacity check counts. Every room counts its seat holders, except an
    /// EXTERNAL_BRIDGE room, whose far-side stand-in holds a seat from creation that is not a person:
    /// its humans are capped without it, so MaxParticipants means "this many WarpTalk users".
    /// </summary>
    private async Task<int> CountSeatsAgainstCapacityAsync(TranslationRoom room, CancellationToken ct)
    {
        if (!TranslationRoomTypes.IsExternalBridge(room.TranslationRoomType))
            return await _participantRepository.CountSeatHoldingParticipantsAsync(room.Id, ct);

        var people = await _participantRepository.CountPeopleInRoomsAsync([room.Id], ct);
        return people?.GetValueOrDefault(room.Id) ?? 0;
    }

    /// <summary>Null when the room exists, is a bridge and is still open; otherwise why not.</summary>
    private async Task<(string Error, string Code)?> LoadOpenBridgeRoomAsync(Guid translationRoomId, CancellationToken ct)
    {
        var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        if (room is null || room.DeletedAt is not null)
            return (TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        if (!TranslationRoomTypes.IsExternalBridge(room.TranslationRoomType))
            return (BridgeRoomConstants.ErrorNotABridgeRoom, ErrorCodes.InvalidState);

        if (BridgeRoomConstants.ClosedStatuses.Contains(room.Status))
            return (BridgeRoomConstants.ErrorRoomClosed, ErrorCodes.InvalidState);

        return null;
    }

    /// <summary>
    /// The create request a claim makes when no room is open for the code: the same shape the web's
    /// auto-room sent to POST /translation-rooms, so language planning, the workspace's
    /// canCreateMeetings / language policy and the stand-in seed are all the ordinary create's.
    /// </summary>
    private static CreateTranslationRoomRequest BuildBridgeCreateRequest(
        ClaimBridgeRoomRequest request, Guid workspaceId, string meetCode) =>
        new(
            WorkspaceId: workspaceId,
            Title: BridgeRoomConstants.DefaultTitle,
            Description: null,
            TranslationRoomType: TranslationRoomTypes.ExternalBridge,
            MaxParticipants: null,
            SourceLanguage: request.SourceLanguage,
            TargetLanguages: request.TargetLanguages?.Where(l => !string.IsNullOrWhiteSpace(l)).ToList(),
            Settings: null,
            ScheduledAt: null,
            InvitedEmails: null,
            ExternalProvider: TranslationRoomConstants.ExternalProviderGoogleMeet,
            ExternalMeetingUrl: GoogleMeetCode.ToUrl(meetCode),
            ExternalMeetingLanguage: request.ExternalMeetingLanguage);

    /// <summary>
    /// The roster name: what the client sent, else the account's own name, else the email. Never
    /// the "Host" fallback — a member is not the host.
    /// </summary>
    private async Task<string> ResolveBridgeDisplayNameAsync(
        string? requested, Guid userId, string? userEmail, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();

        try
        {
            var name = await _userSettingsDirectory.GetDisplayNameAsync(userId, ct);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not resolve a display name for {UserId} joining a bridge room.", userId);
        }

        return string.IsNullOrWhiteSpace(userEmail) ? "Participant" : userEmail;
    }
}
