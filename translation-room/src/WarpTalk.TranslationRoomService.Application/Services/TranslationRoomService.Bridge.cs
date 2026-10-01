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
                    return Result.Success(new ClaimBridgeRoomResponse(
                        created.Value!,
                        hostRow is null ? null : TranslationRoomParticipantMapper.ToDto(hostRow),
                        BridgeRoomConstants.RoleCapturer,
                        Created: true,
                        HeartbeatSeconds,
                        LeaseSeconds));
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

        return Result.Success(new ClaimBridgeRoomResponse(
            roomDto,
            joined.Value.Participant,
            role,
            Created: false,
            HeartbeatSeconds,
            LeaseSeconds));
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
