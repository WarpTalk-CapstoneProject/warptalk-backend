using System;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Application.Authorization;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Authorization;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomService : ITranslationRoomService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _participantRepository;
    private readonly ITranslationRoomSessionRepository _translationRoomSessionRepository;
    private readonly ILanguagePolicy _languagePolicy;
    private readonly IAudioRouteEventProcessor _audioRouteEventProcessor;
    private readonly ITranslationRoomAudioRouteService _audioRouteService;
    private readonly IUserSettingsDirectory _userSettingsDirectory;
    private readonly IWorkspaceMeetingPolicy _workspaceMeetingPolicy;
    private readonly IWorkspaceMemberDirectory _workspaceMemberDirectory;
    private readonly WarpTalk.Shared.Interfaces.IEmailService _emailService;
    private readonly IRedisStateRepository? _redisStateRepository;
    private readonly ILogger<TranslationRoomService> _logger;
    private readonly string _frontendBaseUrl;
    private readonly WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? _notificationClient;
    private readonly WarpTalk.Shared.Protos.UserService.UserServiceClient? _userClient;

    private const string MeetingInvitedNotificationType = "MEETING_INVITED";

    /// <summary>
    /// WT-341. Sibling of MEETING_INVITED and MEETING_REMINDER, and deliberately its own type
    /// rather than a second MEETING_INVITED: "you were invited" and "it is happening now" are
    /// different messages, and reusing the invite type would make the two indistinguishable to
    /// anything that groups, counts, or mutes notifications by type.
    /// </summary>
    private const string MeetingStartedNotificationType = "MEETING_STARTED";

    /// <summary>
    /// Cross-process relay every room event already travels on. The SignalR hub lives in the
    /// Gateway process, so this service cannot reach connected clients directly — it publishes
    /// here and TranslationRoomRedisSubscriberService fans out to the room's group.
    /// </summary>
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";

    /// <summary>
    /// WT-322: the relay command TranslationRoomRedisSubscriberService turns into the
    /// "TranslationRoomStarted" SignalR event. Named RoomStarted to sit beside the RoomEnded
    /// command the same channel already carries.
    /// </summary>
    private const string RoomStartedCommand = "RoomStarted";

    /// <summary>
    /// The counterpart of <see cref="RoomStartedCommand"/> for the other half of the switch:
    /// translation stopped, the meeting did not.
    ///
    /// Start and Stop are room-wide — one switch over the whole meeting's transcript — so every
    /// participant has to learn about them, not just the person who pressed the button. Without
    /// this the only signal was each client's own session poll, so for a few seconds after Stop
    /// the others still preferred an interpreter dub that had stopped being produced.
    ///
    /// Carries no state: "translation is off for this room" is the entire message, and the
    /// clients re-read the session list rather than trusting a payload.
    /// </summary>
    private const string TranslationStoppedCommand = "TranslationStopped";

    /// <summary>
    /// WT-187: the channel NotificationRedisSubscriberService relays to the
    /// "workspace:{workspaceId}" SignalR group, which the web client's
    /// RealtimeNotificationProvider joins via SubscribeWorkspace. Anything published here
    /// reaches every connected workspace member as a "MeetingEvent" and makes them
    /// invalidate their rooms list.
    /// </summary>
    private const string MeetingEventsChannel = "warptalk:meetings:events";

    /// <summary>
    /// WT-187: event type for "people were just invited to this room". Deliberately NOT one of
    /// MeetingCreated/MeetingStatusChanged/MeetingStarted/MeetingDeleted: the Gateway relay
    /// sends every event twice — once as "MeetingEvent" and once under its own eventType — and
    /// the web client binds the same handler to both names, so reusing one of those would fire
    /// the handler (and its toast) twice per publish. An unbound name arrives only via
    /// "MeetingEvent", giving exactly one silent list refresh.
    /// </summary>
    private const string MeetingInvitedEventType = "MeetingInvited";

    /// <summary>
    /// The invitation states this service writes. PENDING is set at creation; ACCEPTED is the
    /// invitee's own answer. Both are already in
    /// <see cref="RoomReadAccess.InvitationStatusesGrantingRead"/>, so accepting never changes
    /// what a person can see — it records that they said yes.
    /// </summary>
    private const string InvitationAcceptedStatus = "ACCEPTED";

    /// <summary>Written by nothing today; read here so Accept fails closed if it ever is.</summary>
    private const string InvitationDeclinedStatus = "DECLINED";

    public TranslationRoomService(
        IUnitOfWork unitOfWork,
        ILanguagePolicy languagePolicy,
        IAudioRouteEventProcessor audioRouteEventProcessor,
        ITranslationRoomAudioRouteService audioRouteService,
        IUserSettingsDirectory userSettingsDirectory,
        IWorkspaceMeetingPolicy workspaceMeetingPolicy,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        WarpTalk.Shared.Interfaces.IEmailService emailService,
        ILogger<TranslationRoomService> logger,
        IOptions<AppSettings>? appSettings = null,
        IRedisStateRepository? redisStateRepository = null,
        // Optional so every existing construction site — and the whole test suite — keeps
        // working. A room service that cannot reach the notification mesh still creates
        // rooms and still sends the invitation email; it just cannot ring the bell.
        WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? notificationClient = null,
        WarpTalk.Shared.Protos.UserService.UserServiceClient? userClient = null)
    {
        _notificationClient = notificationClient;
        _userClient = userClient;
        _unitOfWork = unitOfWork;
        _languagePolicy = languagePolicy;
        _audioRouteEventProcessor = audioRouteEventProcessor;
        _audioRouteService = audioRouteService;
        _userSettingsDirectory = userSettingsDirectory;
        _workspaceMeetingPolicy = workspaceMeetingPolicy;
        _workspaceMemberDirectory = workspaceMemberDirectory;
        _emailService = emailService;
        _redisStateRepository = redisStateRepository;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _participantRepository = _unitOfWork.TranslationRoomParticipantRepository;
        _translationRoomSessionRepository = _unitOfWork.TranslationRoomSessionRepository;
        _logger = logger;
        _frontendBaseUrl = appSettings?.Value.FrontendBaseUrl ?? "http://localhost:3000";
    }

    /// <summary>
    /// WT-187: tell connected workspace members that this room's invitation list changed, so
    /// their rooms list refetches instead of showing a stale list until a manual reload.
    /// Invitations are keyed by email and this service cannot resolve an email to a user id, so
    /// the event is workspace-scoped rather than addressed to the invitee: it only triggers a
    /// refetch, and the list endpoint still applies its own authorization, so a member who
    /// cannot see the room simply refetches the same list they already had.
    /// Never throws — an unnotified client is stale, but the invitations are already persisted
    /// and the invitation emails already sent, so failing the caller here would be worse.
    /// </summary>
    /// <summary>
    /// Rings the bell for someone who was just invited.
    ///
    /// MeetingInvited already goes out on the meeting-events channel, but that is a
    /// workspace-scoped "your room list changed" nudge, not a notification — its own doc says
    /// so. Nothing addressed the invitee, so an invitation existed as an email and a row and
    /// never as anything the app could show them.
    ///
    /// Invitations are keyed by EMAIL and notifications by USER ID, which is the whole reason
    /// this needs a lookup: an invitee who has no account yet has nowhere to receive a
    /// notification, and the email is correctly the only channel for them. That is a silent
    /// skip, not a failure.
    ///
    /// Never throws. The invitation is already persisted and the email already sent by the
    /// time this runs; failing the caller over the bell would trade the thing that matters
    /// for the thing that is nice to have.
    /// </summary>
    private async Task NotifyInvitedUserAsync(
        string email,
        TranslationRoom room,
        string meetingLink,
        CancellationToken ct)
    {
        // WT-415: both skips are logged now, and this is the whole point of the change.
        //
        // MEETING_INVITED has produced ZERO rows in production while its siblings
        // MEETING_STARTED (127) and MEETING_REMINDER (20) fire normally, against 538
        // invitation rows. It is not throwing — its own catch below has never logged either —
        // so it is returning at one of the two guards, and neither said which. That is the
        // same shape as every other silent exit found this week: the branch that swallows a
        // feature has to say so, or the next investigation restarts from nothing.
        //
        // Warning, not information: neither of these is a normal outcome for an invitee who
        // has an account, which is the case the bell exists for.
        if (_notificationClient is null || _userClient is null)
        {
            _logger.LogWarning(
                "invite_notification_skipped: reason=clients_unavailable RoomId={RoomId} "
                + "NotificationClient={HasNotificationClient} UserClient={HasUserClient}. "
                + "The invitation and its email are unaffected.",
                room.Id,
                _notificationClient is not null,
                _userClient is not null);
            return;
        }

        try
        {
            WarpTalk.Shared.Protos.GetUserResponse? user;
            try
            {
                user = await _userClient.GetUserByEmailAsync(
                    new WarpTalk.Shared.Protos.GetUserByEmailRequest
                    {
                        // Invitations store the address as typed; accounts are matched
                        // case-insensitively upstream, but trimming here removes the one
                        // difference a pasted address reliably introduces.
                        Email = email.Trim(),
                    },
                    cancellationToken: ct);
            }
            catch (RpcException notFound) when (notFound.StatusCode == StatusCode.NotFound)
            {
                // UserServiceGrpc.GetUserByEmail THROWS NotFound for an unknown address — it does
                // not answer with an empty id. The `user?.Id` check below was therefore
                // unreachable for its own case, and every invitee without an account fell into
                // the outer catch and was logged as a FAILURE of the notification system. It is
                // not a failure: their invitation email is the correct and only channel.
                _logger.LogInformation(
                    "invite_notification_skipped: reason=no_account_for_email RoomId={RoomId}",
                    room.Id);
                return;
            }

            if (string.IsNullOrWhiteSpace(user?.Id))
            {
                _logger.LogInformation(
                    "invite_notification_skipped: reason=no_account_for_email RoomId={RoomId}",
                    room.Id);
                return;
            }

            var request = new WarpTalk.Shared.Protos.SendNotificationRequest
            {
                UserId = user.Id,
                Type = MeetingInvitedNotificationType,
                Title = $"You were invited to \"{room.Title}\"",
                Body = room.ScheduledAt.HasValue
                    ? $"\"{room.Title}\" is scheduled for {room.ScheduledAt.Value:f}."
                    : $"You were invited to join \"{room.Title}\".",
                ActionUrl = meetingLink,
            };
            request.Metadata.Add("room_id", room.Id.ToString());
            request.Metadata.Add("room_title", room.Title);

            await _notificationClient.SendNotificationAsync(request, cancellationToken: ct);

            // The success side too, so "it fired and something downstream dropped it" can be
            // told apart from "it never fired". Without this, a MEETING_INVITED row missing
            // from the notification service is unattributable.
            _logger.LogInformation(
                "invite_notification_sent: RoomId={RoomId} UserId={UserId}", room.Id, user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to send the meeting-invite notification for RoomId {RoomId}. The invitation and its email are unaffected.",
                room.Id);
        }
    }

    private async Task PublishRoomInvitationsChangedAsync(TranslationRoom room)
    {
        if (_redisStateRepository is null)
        {
            return;
        }

        try
        {
            // camelCase deliberately: the Gateway relay reads these with
            // JsonElement.TryGetProperty, which is an exact, case-sensitive match.
            var payload = JsonSerializer.Serialize(new
            {
                eventType = MeetingInvitedEventType,
                workspaceId = room.WorkspaceId.ToString(),
                roomId = room.Id.ToString(),
                title = room.Title,
                status = room.Status
            });

            await _redisStateRepository.PublishAsync(MeetingEventsChannel, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to publish {EventType} for RoomId: {RoomId}. Invitations are saved; workspace members' room lists will refresh on their next reload.",
                MeetingInvitedEventType,
                room.Id);
        }
    }

    /// <summary>
    /// WT-281: the host's own name for the participant row seeded at room creation.
    ///
    /// Falls back to the role label when the directory cannot answer. That degraded value is the
    /// old bug's literal string, and that is intentional: it is only reachable when Auth is
    /// unreachable or does not know the user, and refusing to create the room over a cosmetic
    /// label would be far worse than a roster entry that briefly reads "Host".
    /// </summary>
    private async Task<string> ResolveHostDisplayNameAsync(Guid hostId, CancellationToken ct)
    {
        try
        {
            var name = await _userSettingsDirectory.GetDisplayNameAsync(hostId, ct);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Could not resolve a display name for HostId: {HostId}; seeding the host participant with the role label.",
                hostId);
        }

        return TranslationRoomConstants.HostDisplayNameFallback;
    }

    /// <inheritdoc />
    public async Task<Result<TranslationRoomDto>> CreateTranslationRoomAsync(
        CreateTranslationRoomRequest request,
        Guid hostId,
        CancellationToken ct = default,
        SeriesOccurrenceContext? occurrence = null)
    {
        try
        {
            // WT-65: Fallback to user settings if languages are missing
            var sourceLang = request.SourceLanguage;
            var targetLangs = request.TargetLanguages;

            if (string.IsNullOrWhiteSpace(sourceLang) || targetLangs == null || !targetLangs.Any())
            {
                var userDefaults = await _userSettingsDirectory.GetDefaultsAsync(hostId, ct);
                if (userDefaults != null)
                {
                    sourceLang ??= userDefaults.DefaultSpeakLanguage;
                    if (targetLangs == null || !targetLangs.Any())
                    {
                        targetLangs = new List<string> { userDefaults.DefaultListenLanguage };
                    }
                }
            }

            sourceLang = LanguageHelper.NormalizeLanguageCode(sourceLang);
            targetLangs = targetLangs?.Select(LanguageHelper.NormalizeLanguageCode).ToList();

            // WT-65: Validate Source Language
            if (string.IsNullOrWhiteSpace(sourceLang))
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ValidationSourceLanguageRequired, ErrorCodes.ValidationError);

            if (!await _languagePolicy.IsSupportedAsync(sourceLang))
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ValidationSourceLanguageUnsupported, ErrorCodes.ValidationError);

            // WT-65: Validate Target Languages
            if (targetLangs == null || !targetLangs.Any())
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ValidationTargetLanguagesRequired, ErrorCodes.ValidationError);

            foreach (var lang in targetLangs)
            {
                if (!await _languagePolicy.IsSupportedAsync(lang))
                    return Result.Failure<TranslationRoomDto>(string.Format(TranslationRoomConstants.ValidationLanguageUnsupported, lang), ErrorCodes.ValidationError);
            }

            // Same gate as the settings-update path: an unrecognised artifact-access level must
            // never reach the database, where it is indistinguishable from HOST_ONLY and quietly
            // denies every participant the host meant to admit.
            if (request.Settings?.ArtifactAccess is { } requestedArtifactAccess
                && !ArtifactAccessLevels.IsValid(requestedArtifactAccess))
            {
                return Result.Failure<TranslationRoomDto>(
                    string.Format(
                        TranslationRoomConstants.ValidationArtifactAccessUnsupported,
                        requestedArtifactAccess,
                        string.Join(", ", ArtifactAccessLevels.All)),
                    ErrorCodes.ValidationError);
            }

            // WT-249: the workspace owns who may open a room — a member whose host permission was
            // revoked must be stopped here. Runs after language resolution so the workspace also
            // gets to veto the languages actually being used, not the ones merely requested.
            var workspaceId = request.WorkspaceId ?? Guid.Empty;
            if (workspaceId == Guid.Empty)
                return Result.Failure<TranslationRoomDto>(ApiMessageConstants.ValidationMessages.WorkspaceRequired, ErrorCodes.ValidationError);

            var policy = await _workspaceMeetingPolicy.ValidateMeetingCreationAsync(workspaceId, hostId, targetLangs, ct);
            if (!policy.IsSuccess)
            {
                // The workspace owns the wording, but it is free to deny without one.
                var reason = string.IsNullOrWhiteSpace(policy.Error)
                    ? "You do not have permission to create meetings in this workspace."
                    : policy.Error;
                return Result.Failure<TranslationRoomDto>(reason, policy.ErrorCode);
            }

            // 1. Determine initial status
            var status = request.ScheduledAt.HasValue ? "SCHEDULED" : "WAITING";

            // 2. The room code.
            //
            // Every occurrence of a recurring booking SHARES one code. A daily standup is one
            // meeting to the person who booked it, and it has to be one thing to share: thirty
            // codes for one standup meant the invite you sent on Monday opened Monday's room
            // forever. GetByCodeAsync resolves a shared code to the occurrence that is live now,
            // or the next one due — so the same link lands on today's meeting every day.
            //
            // The code is carried on the occurrence context rather than stored on the series row,
            // so this needed no migration: the materialiser reads it off an occurrence that
            // already exists and hands it to the next one.
            string roomCode;
            if (occurrence?.SharedRoomCode is { Length: > 0 } sharedCode)
            {
                roomCode = sharedCode;
            }
            else
            {
                bool exists;
                do
                {
                    roomCode = RoomCodeGenerator.GenerateCode();
                    exists = await _translationRoomRepository.ExistsByCodeAsync(roomCode, TranslationRoomConstants.TerminalStatuses, ct);
                } while (exists);
            }

            // 3. Create entity
            var room = request.ToEntity(hostId, roomCode, status, sourceLang, targetLangs);

            // WT-327: an occurrence of a recurring series is an ORDINARY room that additionally
            // knows which series and which local day it belongs to. The (series_id,
            // series_occurrence_local_date) unique index is what makes the materialisation
            // sweep idempotent, so both are stamped before the insert, never afterwards.
            room.SeriesId = occurrence?.SeriesId;
            room.SeriesOccurrenceLocalDate = occurrence?.LocalDate;

            // 4. Save via repository and UnitOfWork
            await _translationRoomRepository.AddAsync(room, ct);

            // WT-281: the host row used to be seeded with the literal string "Host", which is
            // exactly what production rendered in the roster. Resolved through the same Auth
            // directory this method already uses for language defaults.
            var hostDisplayName = await ResolveHostDisplayNameAsync(hostId, ct);

            // WT-82 / WT-281: auto-add the host as a participant so they exist in the DB, with
            // the right speak/listen pair. WT-327 moved the rules themselves into
            // TranslationRoomMapper.BuildHostParticipant so the recurring-series materialiser
            // seeds its occurrences identically instead of from a second hand-written copy.
            var hostParticipant = TranslationRoomMapper.BuildHostParticipant(
                room.Id, hostId, hostDisplayName, sourceLang, targetLangs, room.TranslationRoomType);
            await _participantRepository.AddAsync(hostParticipant, ct);

            // An external-bridge room is the host plus a stand-in for the Google Meet / Zoom call
            // they are actually sitting in. The stand-in is seeded here, at creation, rather than
            // when the room starts, because the audio mesh is built from whoever holds a seat and
            // a bridge room with one seat would generate no routes at all.
            if (TranslationRoomTypes.IsExternalBridge(room.TranslationRoomType))
            {
                await _participantRepository.AddAsync(
                    TranslationRoomMapper.BuildExternalBridgeParticipant(room.Id, sourceLang, targetLangs),
                    ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            await PublishRoomTargetLanguagesAsync(room, ct);

            // Send invitations
            if (request.InvitedEmails != null && request.InvitedEmails.Any())
            {
                // WT-327: every occurrence of a recurring series gets invitation ROWS — without
                // them an invitee would not see days 2..N in their meeting list at all, because
                // GetTranslationRoomsAsync resolves an invitee's visibility through exactly this
                // table. Only the occurrence created at series-creation time sends the EMAIL:
                // thirty identical "you're invited" messages for one daily booking is spam.
                var sendInvitationEmails = occurrence is null || occurrence.SendInvitationEmails;

                var meetingLink = $"{_frontendBaseUrl}/room/{roomCode}";
                var scheduledTime = request.ScheduledAt?.ToString("f") ?? "Now";
                var invitationRepo = _unitOfWork.TranslationRoomInvitationRepository;

                var emailTasks = new List<Task>();

                foreach (var email in request.InvitedEmails)
                {
                    // 1. Store the invitation
                    await invitationRepo.AddAsync(new TranslationRoomInvitation
                    {
                        TranslationRoomId = room.Id,
                        Email = email,
                        Status = "PENDING"
                    }, ct);

                    // 2. Tell them — both ways.
                    //
                    // Only the email was sent here. NotifyInvitedUserAsync already existed and was
                    // already called when somebody is invited to an EXISTING room, so a meeting
                    // that named its guests up front — the ordinary way to create one — was the
                    // single path that rang no bell. "check noti i mn": there was nothing there
                    // to check, and the only trace was an email to an account that had one.
                    //
                    // Same gate as the email, for the same reason: one daily standup must not
                    // deliver thirty notifications. NotifyInvitedUserAsync skips anyone without an
                    // account and never throws, so this cannot fail a room creation.
                    // Both gated together, and correctly so: EVERY occurrence of a series gets
                    // invitation rows (WT-327 — otherwise days 2..N are invisible in the
                    // invitee's list), so announcing per row would mean thirty notifications for
                    // one daily booking. `sendInvitationEmails` is true only for a plain room or
                    // the series' first occurrence, which is exactly "announce this once".
                    if (sendInvitationEmails)
                    {
                        emailTasks.Add(_emailService.SendMeetingInvitationAsync(email, "Participant", meetingLink, request.Title, scheduledTime, ct));
                        emailTasks.Add(NotifyInvitedUserAsync(email, room, meetingLink, ct));
                    }
                }

                // Save the newly added invitations
                await _unitOfWork.SaveChangesAsync(ct);

                // Send all emails in parallel
                await Task.WhenAll(emailTasks);
            }

            // EVERY new room announces itself, not only one created with email invitations.
            //
            // This call used to sit INSIDE the `if (request.InvitedEmails.Any())` block above, so
            // a room created without typing anybody's email — the ordinary case for a workspace
            // whose members can already see each other's meetings — published nothing at all.
            // The whole realtime chain existed and worked; it simply was never rung. That is why
            // the report was "phải F5" from one tester and "realtime mà, bth có cần reload đâu"
            // from another: both were right, about different ways of creating a room.
            //
            // Still after the invitation SaveChangesAsync, so a client that refetches the instant
            // it receives the event cannot miss rows that are about to be committed.
            await PublishRoomInvitationsChangedAsync(room);

            // 5. Return mapped response. WT-280: seats are counted in the database, after the
            // host row above was committed, so a freshly created room correctly reports 1.
            return Result.Success(room.ToResponseDto(
                await _participantRepository.CountSeatHoldingParticipantsAsync(room.Id, ct)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating translation room for HostId: {HostId}", hostId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while creating the room.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<IEnumerable<TranslationRoomInvitationDto>>> GetTranslationRoomInvitationsAsync(Guid translationRoomId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
            {
                return Result.Failure<IEnumerable<TranslationRoomInvitationDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            }

            var invitationRepo = _unitOfWork.TranslationRoomInvitationRepository;
            var invitations = await invitationRepo.FindAsync(i => i.TranslationRoomId == translationRoomId, ct: ct);

            var dtos = invitations.Select(i => new TranslationRoomInvitationDto(
                i.Id,
                i.TranslationRoomId,
                i.Email,
                i.Status,
                i.CreatedAt,
                i.UpdatedAt
            ));

            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching invitations for TranslationRoomId: {TranslationRoomId}", translationRoomId);
            return Result.Failure<IEnumerable<TranslationRoomInvitationDto>>("An unexpected error occurred while fetching invitations.", ErrorCodes.InternalServerError);
        }
    }

    /// <inheritdoc />
    public async Task<Result<TranslationRoomInvitationDto>> AcceptTranslationRoomInvitationAsync(
        Guid translationRoomId,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default)
    {
        try
        {
            var email = RoomReadAccess.NormalizeEmail(userEmail);
            if (email is null)
            {
                // No email claim means there is no way to match an invitation row at all — the
                // rows are keyed by address, not by user id. Same refusal as "no invitation":
                // this endpoint must not become a way to probe which room ids exist.
                return Result.Failure<TranslationRoomInvitationDto>(
                    TranslationRoomConstants.ErrorInvitationNotFound, ErrorCodes.NotFound);
            }

            var invitationRepo = _unitOfWork.TranslationRoomInvitationRepository;
            var invitation = await invitationRepo.FirstOrDefaultAsync(
                i => i.TranslationRoomId == translationRoomId && i.Email == email, ct: ct);

            if (invitation is null)
            {
                return Result.Failure<TranslationRoomInvitationDto>(
                    TranslationRoomConstants.ErrorInvitationNotFound, ErrorCodes.NotFound);
            }

            // DECLINED is the one state Accept may not walk back: the host has already been told
            // this person is not coming, and reversing it silently would make the roster lie.
            // (Nothing writes DECLINED today; this fails closed for when something does.)
            if (string.Equals(invitation.Status, InvitationDeclinedStatus, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<TranslationRoomInvitationDto>(
                    TranslationRoomConstants.ErrorInvitationDeclined, ErrorCodes.InvalidState);
            }

            if (!string.Equals(invitation.Status, InvitationAcceptedStatus, StringComparison.OrdinalIgnoreCase))
            {
                invitation.Status = InvitationAcceptedStatus;
                invitation.UpdatedAt = DateTime.UtcNow;
                invitationRepo.Update(invitation);
                await _unitOfWork.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
                "invitation_accepted: RoomId={RoomId} UserId={UserId}", translationRoomId, userId);

            return Result.Success(new TranslationRoomInvitationDto(
                invitation.Id,
                invitation.TranslationRoomId,
                invitation.Email,
                invitation.Status,
                invitation.CreatedAt,
                invitation.UpdatedAt));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error occurred while accepting the invitation for TranslationRoomId: {TranslationRoomId}",
                translationRoomId);
            return Result.Failure<TranslationRoomInvitationDto>(
                "An unexpected error occurred while accepting the invitation.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// WT-334: this read had NO authorization. The controller's class-level <c>[Authorize]</c> was
    /// the entire check, and this method took no user at all — so any authenticated user could read
    /// any room in any workspace: title, description, room code, schedule, settings, host.
    ///
    /// The guard is <see cref="CanAccessRoomAsync"/>, i.e. WT-304's
    /// <c>RoomReadAccess.IsReadableBy</c> — the same host-OR-participant-OR-invited-by-email
    /// predicate the rooms list, the artifacts guard and the session read already use. This endpoint
    /// was left out of PR #116 as too wide a blast radius for that change; it is the fourth consumer
    /// now rather than a fifth spelling.
    ///
    /// The refusal is NotFound, not Forbidden, and reuses the not-found message verbatim: a 403
    /// would confirm that a room with this id exists, which is exactly what a cross-tenant prober
    /// wants and is a leak in its own right. The two branches below are deliberately
    /// indistinguishable to the caller.
    /// </summary>
    public async Task<Result<TranslationRoomDto>> GetTranslationRoomAsync(
        Guid translationRoomId,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

            if (translationRoom == null)
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (!await CanAccessRoomAsync(translationRoomId, userId, userEmail, ct))
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            return Result.Success(translationRoom.ToResponseDto(
                await _participantRepository.CountSeatHoldingParticipantsAsync(translationRoom.Id, ct)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching translation room: {RoomId}", translationRoomId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while fetching the room.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomListResponse>> GetTranslationRoomsAsync(GetTranslationRoomsRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default)
    {
        try
        {
            var page = Math.Max(1, request.Page);
            var pageSize = Math.Clamp(request.PageSize, 1, 100);
            var query = (await BuildListableRoomsQueryAsync(userId, userEmail, request.WorkspaceId, ct))
                .Where(r => r.DeletedAt == null && r.IsActive);

            var activeRequest = request with { Status = request.Status ?? "SCHEDULED,WAITING,IN_PROGRESS,PAUSED" };
            query = ApplyRoomFilters(query, activeRequest);

            // WT-327: one row per BOOKING, not per occurrence. Resolved before the count so that
            // "14 meetings" does not appear next to a single collapsed row.
            var seriesRows = request.GroupBySeries
                ? await ResolveSeriesRepresentativesAsync(query, ct)
                : null;

            if (seriesRows is not null)
            {
                var representativeIds = seriesRows.Values.Select(s => s.RepresentativeRoomId).ToList();
                query = query.Where(r => r.SeriesId == null || representativeIds.Contains(r.Id));
            }

            var total = await query.CountAsync(ct);
            var roomEntities = await query
                .OrderByDescending(r => r.StartedAt ?? r.ScheduledAt ?? r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            // WT-280: occupancy is counted in the DATABASE, not by loading each room's roster.
            // Eager-loading would work too, but it would have to be repeated on every path that
            // renders a room and any path that forgot silently reports 0 — which is the bug. One
            // grouped count over the page keeps the number impossible to get accidentally-empty,
            // and transfers a scalar per room instead of every participant row.
            var roomIdsForCounts = roomEntities.Select(r => r.Id).ToList();
            var occupancyByRoom = await _participantRepository.CountSeatHoldingParticipantsByRoomsAsync(
                roomIdsForCounts,
                ct);
            // "Who is here now" and "who turned up" are different questions, and a finished
            // meeting only has an answer to the second.
            var attendedByRoom = await _participantRepository.CountEverJoinedByRoomsAsync(
                roomIdsForCounts,
                ct);

            // The rule behind each collapsed row, read once for the page rather than per row.
            var summaries = seriesRows is null
                ? null
                : await BuildSeriesSummariesAsync(roomEntities, seriesRows, ct);

            var rooms = roomEntities
                .Select(r => ToListItemDto(
                    r,
                    userId,
                    occupancyByRoom.GetValueOrDefault(r.Id),
                    r.SeriesId is Guid seriesId ? summaries?.GetValueOrDefault(seriesId) : null,
                    attendedByRoom.GetValueOrDefault(r.Id)))
                .ToList();

            return Result.Success(new TranslationRoomListResponse(rooms, total, page, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing translation rooms for UserId: {UserId}", userId);
            return Result.Failure<TranslationRoomListResponse>("An unexpected error occurred while listing rooms.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<TranslationRoomListItemDto>>> GetSeriesOccurrencesAsync(
        Guid seriesId,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default)
    {
        try
        {
            // Deliberately NOT filtered by IsActive or by status, unlike the active list: the
            // point of a series view is the whole timeline, so an occurrence that already ended
            // or was skipped has to be visible as ended or skipped. Soft-deleted rows stay out.
            var rooms = await (await BuildListableRoomsQueryAsync(userId, userEmail, workspaceId: null, ct))
                .Where(r => r.DeletedAt == null && r.SeriesId == seriesId)
                .OrderBy(r => r.ScheduledAt ?? r.CreatedAt)
                .ToListAsync(ct);

            if (rooms.Count == 0) return Result.Success(new List<TranslationRoomListItemDto>());

            var occupancyByRoom = await _participantRepository.CountSeatHoldingParticipantsByRoomsAsync(
                rooms.Select(r => r.Id).ToList(), ct);

            return Result.Success(rooms
                .Select(r => ToListItemDto(r, userId, occupancyByRoom.GetValueOrDefault(r.Id)))
                .ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing occurrences of series {SeriesId}.", seriesId);
            return Result.Failure<List<TranslationRoomListItemDto>>(
                "An unexpected error occurred while reading the repeating schedule.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<JoinTranslationRoomResponse>> JoinTranslationRoomAsync(JoinTranslationRoomRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default)
    {
        try
        {
            // WT-65: First check if room exists at all
            var translationRoom = await _translationRoomRepository.GetByCodeAsync(request.TranslationRoomCode, null, ct);
            if (translationRoom == null)
                return Result.Failure<JoinTranslationRoomResponse>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (TranslationRoomConstants.TerminalStatuses.Contains(translationRoom.Status))
            {
                return Result.Failure<JoinTranslationRoomResponse>("This room has already ended or has been cancelled.", ErrorCodes.InvalidState);
            }

            // A suspended workspace admits nobody new. Checked here and not only at creation
            // because a room created while the tenant was live outlives the suspension, and every
            // participant who enters it opens a fresh billable STT/TTS stream.
            //
            // This does NOT evict anyone already connected — see EndTranslationRoomAsync, which
            // stays open precisely so an in-flight call can be wound down rather than cut. The
            // cost is that a participant who drops out of a live call in a workspace suspended
            // mid-meeting cannot reconnect; that is accepted deliberately, because a reconnect is
            // indistinguishable at this layer from a new arrival and admitting one admits both.
            var lifecycle = await _workspaceMeetingPolicy.EnsureWorkspaceCanHostMeetingsAsync(
                translationRoom.WorkspaceId, ct);
            if (!lifecycle.IsSuccess)
            {
                return Result.Failure<JoinTranslationRoomResponse>(lifecycle.Error!, lifecycle.ErrorCode);
            }

            // WT-65: Fallback to user settings for Join
            var speakLang = request.SpeakLanguage;
            var listenLang = request.ListenLanguage;

            if (string.IsNullOrWhiteSpace(speakLang) || string.IsNullOrWhiteSpace(listenLang))
            {
                var userDefaults = await _userSettingsDirectory.GetDefaultsAsync(userId, ct);
                if (userDefaults != null)
                {
                    speakLang ??= userDefaults.DefaultSpeakLanguage;
                    listenLang ??= userDefaults.DefaultListenLanguage;
                }
            }

            // WT-65: Validate Speak/Listen languages via Policy Engine
            string? validationError = await _languagePolicy.ValidateParticipantLanguagesAsync(speakLang, listenLang, translationRoom);

            // BR-006: Upsert participant record
            var participant = await _participantRepository.GetByRoomAndUserAsync(translationRoom.Id, userId, ct);

            // FR-1.4-007: Rejected participant language input MUST NOT be saved or applied to room participation state.
            if (validationError != null)
            {
                return Result.Failure<JoinTranslationRoomResponse>(validationError, ErrorCodes.ValidationError);
            }

            // BR-010: Block KICKED participants
            if (participant != null && participant.Status == TranslationRoomParticipantStatuses.Kicked)
            {
                return Result.Failure<JoinTranslationRoomResponse>(TranslationRoomConstants.ErrorParticipantKicked, ErrorCodes.Forbidden);
            }

            // BR-011 & BR-012: Parse Settings
            bool requiresApproval = true;
            if (!string.IsNullOrEmpty(translationRoom.Settings))
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<TranslationRoomSettings>(translationRoom.Settings);
                requiresApproval = settings?.RequiresApproval ?? true;
            }

            // WT-359: the EFFECTIVE host, not the booker. Reading HostId here was the whole bug:
            // after a Transfer Host, the original host rejoining still matched, and BR-004 in
            // TranslationRoomParticipantMapper.UpdateFrom then re-stamped their role back to HOST —
            // silently undoing the transfer every time they left and came back.
            var isHost = translationRoom.IsHostedBy(userId);

            // WT-262: enforce the room's own capacity. MaxParticipants is stamped at creation from
            // TranslationRoomTypePolicy but was never read by anything, so a VIRTUAL_APPOINTMENT
            // capped at 2 accepted an unbounded roster.
            //
            // Three carve-outs, in order:
            //  - MaxParticipants <= 0 means UNLIMITED, matching how the workspace active-room cap
            //    treats "> 0" in WorkspaceGrpcService.ValidateMeetingCreation. A room that never
            //    got a sane value stored must not become unjoinable.
            //  - The host is never turned away from their own room. They are the one person who
            //    cannot route around a full room, and locking them out strands every guest inside.
            //  - Somebody already holding a seat is re-entering, not taking a new one, so a
            //    reconnect or a repeated join from a CONNECTED participant is never counted twice.
            //    A DISCONNECTED/LEFT participant released their seat and does re-acquire one here.
            if (translationRoom.MaxParticipants > 0 &&
                !isHost &&
                !TranslationRoomParticipantStatuses.HoldsSeat(participant?.Status))
            {
                var seatsTaken = await _participantRepository.CountSeatHoldingParticipantsAsync(translationRoom.Id, ct);
                if (seatsTaken >= translationRoom.MaxParticipants)
                {
                    // Conflict, not Forbidden or InvalidState: the caller is permitted and the room
                    // is in a perfectly valid state — the request just collides with how many people
                    // are in it right now, and it succeeds unchanged once a seat frees up.
                    return Result.Failure<JoinTranslationRoomResponse>(
                        string.Format(TranslationRoomConstants.ErrorRoomAtCapacity, translationRoom.MaxParticipants),
                        ErrorCodes.Conflict);
                }
            }

            if (participant == null)
            {
                participant = request.ToParticipantEntity(
                    translationRoom.Id,
                    userId,
                    speakLang!,
                    listenLang!,
                    requiresApproval,
                    isHost
                );

                await _participantRepository.AddAsync(participant, ct);
            }
            else
            {
                participant.UpdateFrom(
                    request,
                    speakLang!,
                    listenLang!,
                    requiresApproval,
                    isHost
                );

                _participantRepository.Update(participant);
            }

            // Mark invitation as ACCEPTED if userEmail is provided
            if (!string.IsNullOrEmpty(userEmail))
            {
                var invitationRepo = _unitOfWork.TranslationRoomInvitationRepository;
                var invitation = await invitationRepo.FirstOrDefaultAsync(i => i.TranslationRoomId == translationRoom.Id && i.Email == userEmail, ct: ct);
                if (invitation != null && invitation.Status == "PENDING")
                {
                    invitation.Status = "ACCEPTED";
                    invitation.UpdatedAt = DateTime.UtcNow;
                    invitationRepo.Update(invitation);
                }
            }

            await _unitOfWork.SaveChangesAsync(ct);

            // S7. A room that is already running gets this participant's audio routes NOW.
            //
            // Routes used to be generated exactly once, inside StartTranslationRoomAsync, and
            // nothing on this path ever added any — so anyone who joined after Start had no
            // route row at all. Translation and TTS still worked for them (the AI re-reads the
            // live languages hash per utterance), but BaseWorker.is_voice_clone_consented
            // matches against the route rows delivered by AUDIO_ROUTES_UPDATED and fails closed
            // without one: their buffered audio was discarded and they were permanently dubbed
            // in a hashed default voice instead of their own. Voice cloning is the headline
            // feature, and it silently switched itself off for anyone a minute late.
            //
            // Only for IN_PROGRESS: a join before Start is already covered by
            // StartTranslationRoomAsync's own GenerateRoutesAsync, and doing it twice would
            // charge every pre-start join for work Start is about to redo.
            //
            // Best-effort, exactly like the Start-path call it complements: a routing failure
            // must not turn a successful join into a failed one. The participant is saved above
            // and this only affects which voice they are dubbed in.
            if (translationRoom.Status == "IN_PROGRESS")
            {
                var routeResult = await _audioRouteService.AddRoutesForParticipantAsync(translationRoom.Id, participant.Id, ct);
                if (!routeResult.IsSuccess)
                {
                    _logger.LogWarning(
                        "Could not add audio routes for participant {ParticipantId} joining in-progress room {RoomId}: {Error}",
                        participant.Id, translationRoom.Id, routeResult.Error);
                }
                else
                {
                    // S8: the routes just created are PENDING, and PENDING is what the client
                    // renders as "Waiting". The room is already running, so they are ready the
                    // moment they exist. Idempotent for every route already broadcasting.
                    //
                    // WT-339: "the room is running" is not "translation is running". A late joiner
                    // arriving before the host presses Start gets configured routes that wait with
                    // everyone else's; PublishRouteReadinessAsync draws that line itself, so this
                    // call needs no condition of its own.
                    await PublishRouteReadinessAsync(translationRoom.Id, ct);
                }
            }

            // WT-428: the knock. A row that just landed WAITING is invisible to the host until
            // the 3s roster poll happens to run — this rings them the moment somebody is actually
            // standing at the door. Best-effort like every realtime publish on this path.
            if (participant.Status == TranslationRoomParticipantStatuses.Waiting)
            {
                await PublishParticipantWaitingAsync(translationRoom.Id, userId, participant.DisplayName);
            }

            // BR-008: Return comprehensive context
            return Result.Success(new JoinTranslationRoomResponse(
                translationRoom.ToResponseDto(
                    await _participantRepository.CountSeatHoldingParticipantsAsync(translationRoom.Id, ct)),
                TranslationRoomParticipantMapper.ToDto(participant)
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while joining translation room. UserId: {UserId}, RoomCode: {RoomCode}", userId, request.TranslationRoomCode);
            return Result.Failure<JoinTranslationRoomResponse>("An unexpected error occurred while joining the room.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// WT-433 (Linear): join a room by its ID — the shape a shared LINK produces — for callers who
    /// are members of the room's workspace.
    ///
    /// The by-code path performs no read authorization at all: possession of the code is the
    /// entitlement. A link is weaker (it appears in address bars, chat logs and screenshots), so
    /// this path demands workspace membership before delegating — and answers NotFound rather
    /// than Forbidden for a non-member, indistinguishable from a missing room, exactly like the
    /// detail read (WT-334).
    ///
    /// Delegation reuses the by-code body verbatim, so requires_approval still lands the row at
    /// WAITING and the host still gets the knock — this endpoint is how an uninvited teammate
    /// reaches the waiting room instead of "Room information is unavailable."
    /// </summary>
    public async Task<Result<JoinTranslationRoomResponse>> JoinTranslationRoomByIdAsync(
        Guid translationRoomId,
        JoinTranslationRoomRequest request,
        Guid userId,
        string? userEmail = null,
        CancellationToken ct = default)
    {
        var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        if (translationRoom == null || translationRoom.DeletedAt != null)
        {
            return Result.Failure<JoinTranslationRoomResponse>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
        }

        var isMember = await _workspaceMemberDirectory.IsMemberAsync(translationRoom.WorkspaceId, userId, ct);
        if (!isMember)
        {
            return Result.Failure<JoinTranslationRoomResponse>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
        }

        return await JoinTranslationRoomAsync(
            request with { TranslationRoomCode = translationRoom.TranslationRoomCode },
            userId,
            userEmail,
            ct);
    }

    /// <summary>
    /// Tells the gateway somebody is waiting for admission, so the HOST learns in realtime
    /// instead of on the next roster poll. Mirrors PublishParticipantAdmittedAsync's channel and
    /// failure posture: a lost knock costs immediacy only — the poll still shows the row.
    /// </summary>
    private async Task PublishParticipantWaitingAsync(Guid translationRoomId, Guid waitingUserId, string? displayName)
    {
        if (_redisStateRepository is null)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                Command = "ParticipantWaiting",
                RoomId = translationRoomId.ToString(),
                UserId = waitingUserId.ToString(),
                DisplayName = displayName ?? string.Empty
            });

            await _redisStateRepository.PublishAsync("warptalk:translation-room:commands", payload);
        }
        catch (Exception publishEx)
        {
            _logger.LogWarning(
                publishEx,
                "Failed to publish ParticipantWaiting for RoomId: {RoomId}, UserId: {UserId}; the host will see them on the next roster poll.",
                translationRoomId, waitingUserId);
        }
    }

    /// <summary>
    /// S8 — drive this room's audio routes out of PENDING and into BROADCASTING.
    ///
    /// GenerateRoutesAsync creates every route at PENDING, and the ONLY transition out of
    /// PENDING is <c>config_ready</c> — an event no code in this repository has ever emitted.
    /// session_starts is only accepted from READY, so it was rejected on every route, and
    /// telemetry_state_updated is rejected outright because PENDING is not a streaming state.
    /// A route therefore sat at PENDING for the entire meeting no matter what happened, and
    /// PENDING is what the client renders as "Waiting" — the status frozen on the projector.
    ///
    /// The missing piece was the producer, not the state table: routes ARE configured at the
    /// moment GenerateRoutesAsync returns, so that is when config_ready is true. Emitting it
    /// here keeps the modelled lifecycle (PENDING -> READY -> BROADCASTING) intact rather than
    /// rewriting the transition table around a step nothing performs.
    ///
    /// Both events are idempotent: a route already BROADCASTING rejects config_ready and
    /// session_starts as invalid transitions, ProcessTransition returns false, and nothing is
    /// written. That is what makes this safe to call again on a late join or a restart.
    ///
    /// This is also what makes the status correct in a room where NOBODY SPEAKS. It is driven
    /// by the room's own lifecycle, so it needs no telemetry payload, no timer, and no
    /// heartbeat — a few seconds of silence at the start of a demo can no longer leave the
    /// status stuck.
    ///
    /// WT-339 — but READY IS AS FAR AS OPENING A ROOM GOES. The two events answer two different
    /// questions and were being emitted as one pair from every caller, which is how opening a
    /// room came to switch translation on:
    ///
    ///   config_ready   — "these routes are configured". True the moment GenerateRoutesAsync
    ///                    returns, whoever asked and whatever the room is doing.
    ///   session_starts — "translation is running on them". True only while a TranslationSession
    ///                    is open, which now happens solely when the host presses Start
    ///                    Translation (ResumeTranslationRoomAsync).
    ///
    /// BROADCASTING is not cosmetic here: AudioRouteEventProcessor publishes AUDIO_ROUTES_UPDATED
    /// on session_starts, and that is the signal livekit_ingress_worker uses to tell a published
    /// meeting microphone apart from translation being active. Emitting it at room open is what
    /// made the AI start transcribing — and billing — before anybody asked it to.
    ///
    /// So the session is looked up rather than assumed. Every caller may still call this
    /// unconditionally: a room open emits config_ready alone, a late join into a room where
    /// translation IS running emits both, and replaying either over a live route is the same
    /// no-op it always was.
    /// </summary>
    private async Task PublishRouteReadinessAsync(Guid translationRoomId, CancellationToken ct)
    {
        await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.config_ready.ToString(), "{}", ct);

        var activeSession = await _translationRoomSessionRepository.GetActiveSessionByRoomIdAsync(translationRoomId, ct);
        if (activeSession == null) return;

        await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.session_starts.ToString(), "{}", ct);
    }

    public async Task<Result> OpenWaitingRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (!translationRoom.IsHostedBy(hostId)) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);

            if (translationRoom.Status != "SCHEDULED")
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToWaiting, ErrorCodes.InvalidState);

            translationRoom.Status = "WAITING";
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening waiting room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomDto>> StartTranslationRoomAsync(
        Guid translationRoomId,
        Guid callerId,
        string? callerEmail,
        CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

            if (translationRoom == null)
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            var permission = await ResolveStartPermissionAsync(translationRoom, callerId, callerEmail, ct);
            if (!permission.IsSuccess)
                return Result.Failure<TranslationRoomDto>(permission.Error!, permission.ErrorCode);

            if (translationRoom.Status == "IN_PROGRESS")
            {
                await PublishRoomTargetLanguagesAsync(translationRoom, ct);

                // S7. This early return used to skip route generation entirely, which is why
                // "just restart the room" never recovered a late joiner who had no route row.
                // The join path now adds routes as people arrive, so this is the repair path
                // for a room that was already running when that fix shipped — and for any
                // future gap, one deliberate host action is a safe place to reconcile the mesh.
                var restartRouteResult = await _audioRouteService.GenerateRoutesAsync(translationRoomId, ct);
                if (!restartRouteResult.IsSuccess)
                    _logger.LogWarning("Could not reconcile audio routes for already-running room {RoomId}: {Error}", translationRoomId, restartRouteResult.Error);

                // S8: and drive any route still sitting at PENDING into BROADCASTING. Idempotent
                // for routes that are already streaming.
                await PublishRouteReadinessAsync(translationRoomId, ct);

                return Result.Success(translationRoom.ToResponseDto(
                    await _participantRepository.CountSeatHoldingParticipantsAsync(translationRoom.Id, ct)));
            }

            if (translationRoom.Status != "SCHEDULED" && translationRoom.Status != "WAITING")
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorInvalidTransitionToStart, ErrorCodes.InvalidState);

            // A suspended workspace may not take a room live. This is the transition that actually
            // turns on billable AI — it opens a translation session and hands the room to the audio
            // routing state machine — so a room scheduled before the suspension must stop here
            // rather than at creation, which already happened.
            //
            // Placed AFTER the IN_PROGRESS short-circuit above on purpose: a room that is already
            // running keeps its idempotent re-Start, because refusing it would strand a host whose
            // client retried mid-call. Suspension stops meetings from starting; it does not end one
            // that has.
            var lifecycle = await _workspaceMeetingPolicy.EnsureWorkspaceCanHostMeetingsAsync(
                translationRoom.WorkspaceId, ct);
            if (!lifecycle.IsSuccess)
                return Result.Failure<TranslationRoomDto>(lifecycle.Error!, lifecycle.ErrorCode);

            // (Re)generate audio routes for the participants currently in the room so speech is
            // routed correctly once translation starts. Routes form a full mesh between
            // participants whose languages differ, so a room with only the host — or where
            // everyone shares a language — legitimately has zero routes and that must NOT block
            // Start. Additional routes for people who arrive after this point are added
            // incrementally by JoinTranslationRoomAsync (S7); this comment used to claim that
            // happened already, and no code path did it. Route generation is best-effort: a
            // failure here should not prevent the host from opening the room.
            var routeResult = await _audioRouteService.GenerateRoutesAsync(translationRoomId, ct);
            if (!routeResult.IsSuccess)
                _logger.LogWarning("Could not generate audio routes while starting room {RoomId}: {Error}", translationRoomId, routeResult.Error);

            translationRoom.Status = "IN_PROGRESS";
            translationRoom.StartedAt ??= DateTime.UtcNow;
            translationRoom.UpdatedAt = DateTime.UtcNow;
            translationRoom.UpdatedBy = callerId;

            _translationRoomRepository.Update(translationRoom);

            await _unitOfWork.SaveChangesAsync(ct);
            await PublishRoomTargetLanguagesAsync(translationRoom, ct);

            // WT-322: tell everyone already in the room that translation is now live. Published
            // after SaveChangesAsync for the same reason RoomEnded is: a client that refetches on
            // the event must not be able to observe the room still WAITING. Failure to notify must
            // not fail the start — the room is IN_PROGRESS and persisted by this point.
            await PublishRoomStartedAsync(translationRoom, ct);

            // S8: routes are created PENDING and the only exit is config_ready, which nothing in
            // the repository has ever emitted — so every route sat at PENDING for the whole
            // meeting and the client rendered "Waiting" regardless of whether anyone spoke.
            // Start is where the routes genuinely become configured, so it is where the event
            // belongs.
            //
            // WT-339: configured, and no further. This call used to take the routes all the way to
            // BROADCASTING, which is why merely opening a room started translation. There is no
            // TranslationSession at this point in the method — nothing above creates one any more —
            // so PublishRouteReadinessAsync stops at READY of its own accord.
            await PublishRouteReadinessAsync(translationRoomId, ct);

            // WT-341: ring the bell for everyone who was invited. Deliberately here and not in the
            // IN_PROGRESS short-circuit above — that path is the idempotent re-Start, and a host
            // whose client retried mid-call must not re-notify the whole invite list. This is the
            // one place the room genuinely crosses from not-started to started.
            //
            // It matters most for the case this change exists for: when somebody other than the
            // host opens the meeting, the host is not the person clicking, so without this the host
            // themselves would have no idea their meeting had begun.
            await NotifyRoomStartedAsync(translationRoom, callerId, ct);

            return Result.Success(translationRoom.ToResponseDto(
                await _participantRepository.CountSeatHoldingParticipantsAsync(translationRoom.Id, ct)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while starting translation room. RoomId: {RoomId}, CallerId: {CallerId}", translationRoomId, callerId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while starting the room.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// WT-341 — "may this caller take the room live?", the whole of it, in one place.
    ///
    /// The rule this replaces was <c>room.HostId != callerId</c>. That is a correct rule for a
    /// meeting whose door the host must answer, and the wrong rule for every other meeting: a host
    /// who is busy, ill, or simply late made the meeting permanently unstartable, and no other
    /// participant — not even the workspace owner — could rescue it.
    ///
    /// <c>RequiresApproval</c> is what separates the two, and it is not a new concept invented for
    /// this: it already decides whether a joiner lands CONNECTED or WAITING
    /// (<see cref="TranslationRoomParticipantMapper"/>), so it already means "entry is the host's
    /// decision". A room that requires approval therefore stays host-only to start, because
    /// starting it would open a room whose lobby only the host can clear. A room that does not
    /// requires no host decision at any point, so requiring one to begin was never protecting
    /// anything.
    ///
    /// Entitlement for the non-host path is <see cref="CanAccessRoomAsync"/> — host OR participant
    /// OR invited-by-email, plus workspace Owner/Admin — the same predicate that decides who may
    /// READ the room. That equivalence is the point: this hands no one a room they could not
    /// already open and sit in. It is emphatically NOT "any authenticated user", which would let a
    /// stranger holding a room id start someone else's meeting and begin billing their workspace
    /// for STT and TTS.
    /// </summary>
    private async Task<Result> ResolveStartPermissionAsync(
        TranslationRoom room,
        Guid callerId,
        string? callerEmail,
        CancellationToken ct)
    {
        if (room.IsHostedBy(callerId))
            return Result.Success();

        // ReadSettings, not a raw Deserialize: it is case-insensitive and falls back to defaults on
        // a malformed blob. A settings column this failed to parse would otherwise read as
        // RequiresApproval=false and hand the room to a non-host — failing OPEN on unreadable data
        // is exactly backwards for a permission check.
        if (TranslationRoomMapper.ReadSettings(room.Settings).RequiresApproval)
        {
            return Result.Failure(
                "This meeting requires the host's approval to join, so only the host can start it.",
                ErrorCodes.Forbidden);
        }

        if (!await CanAccessRoomAsync(room.Id, callerId, callerEmail, ct))
        {
            return Result.Failure(
                "You are not allowed to start this meeting.",
                ErrorCodes.Forbidden);
        }

        return Result.Success();
    }

    /// <summary>
    /// WT-341 — tells the people invited to this meeting that it has begun.
    ///
    /// The room-started Redis command published beside this reaches clients ALREADY IN the room;
    /// it is a live-state push, not a notification, and someone who has not opened the room sees
    /// nothing from it. Before this, a meeting starting was invisible to every invitee who was not
    /// already looking at it — which is survivable when the host starts the meeting they scheduled,
    /// and not survivable once anybody can, because then the host is an invitee too.
    ///
    /// Addressed by INVITATION rather than by participant row on purpose: the people who need
    /// telling are the ones who have not arrived yet. Anyone already in the room learns from the
    /// live push. An invitee with no account has no notification inbox — that is a silent skip,
    /// exactly as in <see cref="NotifyInvitedUserAsync"/>, because their channel was the email.
    ///
    /// Never throws. The room is IN_PROGRESS and persisted by the time this runs; failing the
    /// start over an undelivered bell would trade the meeting for the announcement of it.
    /// </summary>
    private async Task NotifyRoomStartedAsync(TranslationRoom room, Guid startedBy, CancellationToken ct)
    {
        if (_notificationClient is null || _userClient is null)
            return;

        try
        {
            var invitations = await _unitOfWork.TranslationRoomInvitationRepository
                .FindAsync(i => i.TranslationRoomId == room.Id, ct: ct);

            var emails = (invitations ?? Enumerable.Empty<TranslationRoomInvitation>())
                .Where(i => RoomReadAccess.InvitationStatusesGrantingRead.Contains(i.Status))
                .Select(i => RoomReadAccess.NormalizeEmail(i.Email))
                .Where(email => email is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (emails.Count == 0)
                return;

            var meetingLink = $"{_frontendBaseUrl.TrimEnd('/')}/room/{room.Id}";

            foreach (var email in emails)
            {
                await NotifyRoomStartedRecipientAsync(email!, room, meetingLink, startedBy, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to announce the start of RoomId {RoomId}. The room is started and unaffected.",
                room.Id);
        }
    }

    /// <summary>
    /// One recipient, so a single unresolvable invitee cannot silence the rest of the list — the
    /// try/catch is per-person for the same reason the loop exists at all.
    /// </summary>
    private async Task NotifyRoomStartedRecipientAsync(
        string email,
        TranslationRoom room,
        string meetingLink,
        Guid startedBy,
        CancellationToken ct)
    {
        try
        {
            var user = await _userClient!.GetUserByEmailAsync(
                new WarpTalk.Shared.Protos.GetUserByEmailRequest { Email = email },
                cancellationToken: ct);

            if (string.IsNullOrWhiteSpace(user?.Id))
                return;

            // The person who just clicked Start is watching the room open in front of them.
            if (Guid.TryParse(user.Id, out var userId) && userId == startedBy)
                return;

            var request = new WarpTalk.Shared.Protos.SendNotificationRequest
            {
                UserId = user.Id,
                Type = MeetingStartedNotificationType,
                Title = $"\"{room.Title}\" has started",
                Body = $"\"{room.Title}\" is live now. Join when you're ready.",
                ActionUrl = meetingLink,
            };
            request.Metadata.Add("room_id", room.Id.ToString());
            request.Metadata.Add("room_title", room.Title);

            await _notificationClient!.SendNotificationAsync(request, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not notify one invitee that RoomId {RoomId} started; the remaining invitees are unaffected.",
                room.Id);
        }
    }

    /// <summary>
    /// WT-322: broadcast "TranslationRoomStarted" to everyone already sitting in the room.
    ///
    /// The web client has always listened for this event (persistent-meeting-session.tsx) and
    /// nothing ever sent it. A participant who joined BEFORE the host pressed Start therefore kept
    /// <c>warptalkStarted === false</c> until some unrelated refetch happened to notice the status
    /// change — and that flag unsubscribes every interpreter audio track and drops every transcript
    /// and translation segment. FilteredRoomAudio still lets the raw microphones through, so it is
    /// not silence: it is a participant hearing the untranslated original with no interpreter dub
    /// and no captions, while the host sees translation running normally.
    ///
    /// Published on the same Gateway relay channel every other room event uses (see
    /// <see cref="GatewayCommandsChannel"/>): the SignalR hub lives in the Gateway process, so this
    /// service cannot reach connected clients directly. TranslationRoomRedisSubscriberService
    /// forwards <c>State</c> to the "translationRoom:{roomId}" group untouched — the same
    /// pre-serialized-camelCase-payload arrangement PollCreated/QuestionAsked/BreakoutsStarted use.
    ///
    /// The payload is the client's TranslationRoomStateDto. <c>participants</c> is REQUIRED, not
    /// decorative: the client's store does <c>participants: state.participants</c>, so omitting it
    /// would blank the roster of everyone in the room. It carries the CONNECTED participants —
    /// the same definition of "in the room" the roster and the seat count already use — each
    /// shaped exactly like the hub's own ParticipantJoined payload.
    ///
    /// Never throws: an unnotified client is stale, but the room is already IN_PROGRESS and
    /// persisted, so failing the host's Start here would be strictly worse.
    /// </summary>
    private async Task PublishRoomStartedAsync(TranslationRoom room, CancellationToken ct)
    {
        if (_redisStateRepository is null)
        {
            return;
        }

        try
        {
            var participants = await _participantRepository.GetByRoomIdAsync(room.Id, ct);
            var connected = (participants ?? Enumerable.Empty<TranslationRoomParticipant>())
                .Where(p => p.Status == TranslationRoomParticipantStatuses.Connected)
                // Exactly the six fields TranslationRoomHub's ParticipantJoined already sends, in
                // the same order. Deliberately NOT role or status: merge-participants.ts keeps
                // identity and role with the REST roster precisely because the live payload has
                // never carried them, and a live row that suddenly did would start winning
                // where the API row is missing.
                .Select(p => new
                {
                    userId = p.UserId?.ToString() ?? string.Empty,
                    displayName = p.DisplayName,
                    speakLanguage = p.SpeakLanguage,
                    listenLanguage = p.ListenLanguage,
                    isMuted = false,
                    joinedAt = (p.JoinedAt ?? p.CreatedAt).ToUniversalTime()
                })
                .ToList();

            // camelCase deliberately: the Gateway forwards this element to clients as-is, and the
            // web client reads it as TranslationRoomStateDto.
            var payload = JsonSerializer.Serialize(new
            {
                Command = RoomStartedCommand,
                RoomId = room.Id.ToString(),
                State = new
                {
                    translationRoomId = room.Id.ToString(),
                    translationRoomCode = room.TranslationRoomCode,
                    status = room.Status,
                    participants = connected
                }
            });

            await _redisStateRepository.PublishAsync(GatewayCommandsChannel, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to publish {Command} for RoomId: {RoomId}. The room is started; participants already in it will not hear audio or see captions until their own room query refetches.",
                RoomStartedCommand,
                room.Id);
        }
    }

    /// <summary>
    /// Tells everyone in the room that translation is off now.
    ///
    /// Never throws. Translation is already stopped and persisted by the time this runs, and the
    /// clients poll the session list anyway — an undelivered broadcast costs a few seconds of
    /// staleness, whereas failing the host's Stop over it would leave translation running.
    /// </summary>
    private async Task PublishTranslationStoppedAsync(TranslationRoom room, CancellationToken ct)
    {
        if (_redisStateRepository is null)
            return;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                Command = TranslationStoppedCommand,
                RoomId = room.Id.ToString()
            });

            await _redisStateRepository.PublishAsync(GatewayCommandsChannel, payload);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "Failed to publish {Command} for RoomId: {RoomId}. Translation is stopped; participants' clients will notice on their next session poll.",
                TranslationStoppedCommand,
                room.Id);
        }
    }

    private async Task PublishRoomTargetLanguagesAsync(TranslationRoom room, CancellationToken ct)
    {
        if (_redisStateRepository is null)
            return;

        try
        {
            var targetLanguages = LanguageHelper.ParseTargetLanguages(room.TargetLanguages);
            await _redisStateRepository.StringSetAsync(
                $"meeting:{room.Id}:target_languages",
                JsonSerializer.Serialize(targetLanguages),
                TimeSpan.FromHours(24));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to publish target languages for room {RoomId}", room.Id);
        }
    }

    public async Task<Result> PauseTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (!translationRoom.IsHostedBy(hostId)) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);

            if (translationRoom.Status != "IN_PROGRESS")
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToPaused, ErrorCodes.InvalidState);

            translationRoom.Status = "PAUSED";
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);

            // Pausing closes the current translation session — Resume opens a fresh, newly
            // numbered one rather than reopening this one.
            await EndActiveTranslationSessionAsync(translationRoomId, ct);

            await _unitOfWork.SaveChangesAsync(ct);

            // WT-67: Trigger Audio Routing State Machine to Pause
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.room_pause.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ResumeTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        var transactionStarted = false;
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // WT-373: the room's own rule, not a bare host check.
            //
            // This was `IsHostedBy(hostId)`, and WT-371's "participants may start translation"
            // was implemented only in TranslationRoomSessionService.CanStartSessionAsync — which
            // serves POST /sessions, an endpoint the client does not call. Start Translation
            // calls /resume, so the rule was enforced where nothing runs and ignored where
            // everything does.
            //
            // The cost was the whole feature: /resume is the only path that opens a
            // TranslationRoomSession, and that row IS `translation_active` in
            // PublishRoutesUpdateAsync, which the AI worker gates every STT result on. A
            // participant in an opted-in room was shown the button, pressed it, and got 401 from
            // a branch that returns without logging — no session, no dub, and nothing anywhere
            // saying why.
            //
            // Stopping stays host-only below and in StopTranslationAsync: opening a meeting up is
            // not the same as letting anyone cut it off for everybody.
            if (!await RoomStartTranslationAccess.CanStartTranslationAsync(
                    translationRoom, hostId, _workspaceMemberDirectory, _participantRepository, ct))
            {
                return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);
            }

            // WT-339: THIS is "Start Translation". IN_PROGRESS is accepted alongside PAUSED
            // because opening the room no longer starts translation with it — an open, never-yet-
            // translated room is exactly the state the host presses the button from. Starting and
            // resuming really are the same act on the same room; the transcript tells them apart
            // by session number, not by which endpoint was called.
            if (translationRoom.Status != "PAUSED" && translationRoom.Status != "IN_PROGRESS")
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToInProgress, ErrorCodes.InvalidState);

            await _unitOfWork.BeginTransactionAsync(ct);
            transactionStarted = true;

            // Double-clicks and retries may arrive at different service instances. Serialize the
            // check-and-create section in PostgreSQL so they cannot both observe "no session".
            await _translationRoomSessionRepository.AcquireSessionStartLockAsync(translationRoomId, ct);

            translationRoom.Status = "IN_PROGRESS";
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);

            // See StartTranslationRoomAsync — Resume opens a new numbered session too.
            await StartNewTranslationSessionAsync(translationRoom, ct);

            await _unitOfWork.SaveChangesAsync(ct);
            await _unitOfWork.CommitTransactionAsync(ct);
            transactionStarted = false;
            await PublishRoomStartedAsync(translationRoom, ct);

            // WT-339: the routes are only now allowed to broadcast. Emitted AFTER SaveChangesAsync
            // so the session this method just opened is readable — PublishRouteReadinessAsync
            // looks it up rather than taking anyone's word for it, and would otherwise stop at
            // READY on the very call that is meant to start translation.
            //
            // Covers the START case (routes sitting at READY since the room was opened: config_ready
            // is a no-op, session_starts takes them to BROADCASTING). room_resume below covers the
            // RESUME case (routes PAUSED), where the readiness pair is the no-op instead. Each is
            // an invalid transition in the other's state, so both are safe to send every time.
            await PublishRouteReadinessAsync(translationRoomId, ct);

            // WT-67: Trigger Audio Routing State Machine to Resume
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.room_resume.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            if (transactionStarted)
            {
                try
                {
                    await _unitOfWork.RollbackTransactionAsync(ct);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(rollbackException, "Error rolling back translation start. RoomId: {RoomId}", translationRoomId);
                }
            }

            _logger.LogError(ex, "Error resuming translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    /// <inheritdoc />
    public async Task<Result> StopTranslationAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (!translationRoom.IsHostedBy(hostId)) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);

            // Only a live room can stop translating. A PAUSED room is not translating either, but
            // resuming it is a different act with a different endpoint, and quietly accepting the
            // stop here would leave the caller believing the room was still live.
            if (translationRoom.Status != "IN_PROGRESS")
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToPaused, ErrorCodes.InvalidState);

            // The room's status is deliberately untouched. IN_PROGRESS means the MEETING is open,
            // which it still is — that is the whole point of stopping translation rather than
            // pausing the room, and it is what keeps the transcript running.
            await EndActiveTranslationSessionAsync(translationRoomId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            // Emitted after the save so the routes-update this triggers reads the session as ended
            // — translation_active is computed from that row, and publishing first would announce
            // translation as still running.
            await _audioRouteEventProcessor.ProcessEventAsync(
                translationRoomId,
                null,
                AudioRoutingEventType.translation_stopped.ToString(),
                "{}",
                ct);

            await PublishTranslationStoppedAsync(translationRoom, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping translation. RoomId: {RoomId}", translationRoomId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomDto>> CancelTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

            if (translationRoom == null)
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (!translationRoom.IsHostedBy(hostId))
                return Result.Failure<TranslationRoomDto>("Only the host can cancel the room.", ErrorCodes.Forbidden);

            if (translationRoom.Status != "SCHEDULED" && translationRoom.Status != "WAITING")
                return Result.Failure<TranslationRoomDto>("Only scheduled or waiting rooms can be cancelled.", ErrorCodes.InvalidState);

            translationRoom.Status = "CANCELLED";
            translationRoom.EndedAt ??= DateTime.UtcNow;
            translationRoom.UpdatedAt = DateTime.UtcNow;
            translationRoom.UpdatedBy = hostId;

            _translationRoomRepository.Update(translationRoom);

            var participants = await _participantRepository.GetByRoomIdAsync(translationRoomId, ct);
            if (participants != null)
            {
                var participantsToUpdate = participants
                    .Where(p => p.Status == TranslationRoomParticipantStatuses.Connected ||
                                p.Status == TranslationRoomParticipantStatuses.Waiting)
                    .ToList();

                foreach (var participant in participantsToUpdate)
                {
                    participant.Status = TranslationRoomParticipantStatuses.Disconnected;
                    participant.UpdatedAt = DateTime.UtcNow;
                    _participantRepository.Update(participant);
                }
            }

            await _unitOfWork.SaveChangesAsync(ct);

            // WT-314: a cancelled room must reach the AI pipeline as a terminal room status,
            // exactly like an ended one. MeetingRoomService summons livekit_ingress_worker's
            // "AIBot_{room}" on every JoinMeetingAsync, and that bot's only exit is an
            // AUDIO_ROUTES_UPDATED carrying a terminal status. Cancel is reachable only from
            // SCHEDULED/WAITING — states that by definition have no audio routes yet — so
            // nothing else on this path would ever publish, and the bot stayed connected
            // billing LiveKit connection minutes. Published after SaveChangesAsync so the
            // status on the wire is the persisted CANCELLED, and best-effort: the room is
            // already cancelled by this point and must not be failed by a notification fault.
            await PublishTerminalLifecycleAsync(translationRoomId, "cancelling", ct);

            return Result.Success(translationRoom.ToResponseDto(
                await _participantRepository.CountSeatHoldingParticipantsAsync(translationRoom.Id, ct)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while cancelling translation room. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while cancelling the room.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ExpireTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // Idempotent check
            if (translationRoom.Status == "EXPIRED")
                return Result.Success();

            if (translationRoom.Status != "SCHEDULED" && translationRoom.Status != "WAITING")
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToExpired, ErrorCodes.InvalidState);

            translationRoom.Status = "EXPIRED";
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);

            var participants = await _participantRepository.GetByRoomIdAsync(translationRoomId, ct);
            if (participants != null)
            {
                var participantsToUpdate = participants
                    .Where(p => p.Status == TranslationRoomParticipantStatuses.Connected ||
                                p.Status == TranslationRoomParticipantStatuses.Waiting)
                    .ToList();

                foreach (var participant in participantsToUpdate)
                {
                    participant.Status = TranslationRoomParticipantStatuses.Disconnected;
                    participant.UpdatedAt = DateTime.UtcNow;
                    _participantRepository.Update(participant);
                }
            }

            await _unitOfWork.SaveChangesAsync(ct);

            // WT-314: same door as Cancel above. Expiry is driven by IdleRoomMonitoringWorker
            // on rooms nobody ever started, which is precisely the population that has no
            // audio routes — so without this publish the ingress bot for an expired room was
            // never told to leave.
            await PublishTerminalLifecycleAsync(translationRoomId, "expiring", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error expiring translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Announce a terminal room lifecycle to the AI pipeline (WT-314).
    ///
    /// AudioRouteEventProcessor turns this into an AUDIO_ROUTES_UPDATED carrying the room's
    /// now-persisted status, which is the only signal that releases livekit_ingress_worker's
    /// per-room bot — and therefore the only thing that stops LiveKit connection minutes
    /// accruing. Best-effort by design: the room has already been persisted in its terminal
    /// state, so a Redis or processor fault must not turn a successful cancel/expire into a
    /// failure for the caller. The worker's own idle sweep is the backstop if this is lost.
    /// </summary>
    private async Task PublishTerminalLifecycleAsync(Guid translationRoomId, string operation, CancellationToken ct)
    {
        try
        {
            await _audioRouteEventProcessor.ProcessEventAsync(
                translationRoomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to publish session_ends while {Operation} room {RoomId}. The room is persisted; the ingress bot will be released by its idle sweep instead.",
                operation,
                translationRoomId);
        }
    }

    public async Task<Result> EndTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

            if (translationRoom == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // Host OR workspace Owner/Admin — RoomHostAccess, the rule WT-188 established and WT-313
            // reconciled. This site was still spelling it as "is the original host", and the
            // mismatch strands rooms.
            //
            // Ending a meeting is a two-call client-side saga with no server-side reconciliation:
            // "End for everyone" calls MeetingService and then this endpoint.
            // MeetingRoomService.EndMeetingAsync accepts the ACTIVE host (isOriginalHost ||
            // isActiveHost); this accepted only the ORIGINAL one. So after a host transfer the
            // first call tore down LiveKit and marked the meeting FINISHED, the second was refused,
            // and the translation room stayed IN_PROGRESS forever — never reaching History, and
            // repaired by nothing, since ExpireTranslationRoomAsync has no production callers. A
            // network blip between the two calls leaves the same orphan.
            //
            // Widening to workspace Owner/Admin does not close the mismatch completely: an active
            // host who is a plain workspace member is still refused, because ActiveHostId lives in
            // MeetingService's own table and is not a fact this service can see. Making the two
            // agree by construction needs the active host propagated here (or read over gRPC), and
            // that is a larger change than this one. What this does buy is that the orphan is
            // always recoverable by an Owner/Admin rather than permanent.
            if (!await RoomHostAccess.HasHostAuthorityAsync(translationRoom, hostId, _workspaceMemberDirectory, ct))
                return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedEndRoom, ErrorCodes.Unauthorized);

            if (translationRoom.Status == "ENDED")
                return Result.Success();
            if (translationRoom.Status != "IN_PROGRESS" && translationRoom.Status != "PAUSED" && translationRoom.Status != "WAITING")
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToEnded, ErrorCodes.InvalidState);

            translationRoom.Status = "ENDED";
            translationRoom.EndedAt = DateTime.UtcNow;
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);

            // Room may end directly from IN_PROGRESS (no prior Pause) — close whatever
            // translation session is still open so it gets an EndedAt.
            await EndActiveTranslationSessionAsync(translationRoomId, ct);

            var participants = await _participantRepository.GetByRoomIdAsync(translationRoomId, ct);
            if (participants != null)
            {
                var participantsToUpdate = participants
                    .Where(p => p.Status == TranslationRoomParticipantStatuses.Connected ||
                                p.Status == TranslationRoomParticipantStatuses.Waiting)
                    .ToList();

                foreach (var participant in participantsToUpdate)
                {
                    participant.Status = TranslationRoomParticipantStatuses.Disconnected;
                    participant.UpdatedAt = DateTime.UtcNow;
                    _participantRepository.Update(participant);
                }
            }

            await _unitOfWork.SaveChangesAsync(ct);

            // WT-191: tell everyone still in the room that it is over. The host ends the meeting
            // over REST, so TranslationRoomHub.EndTranslationRoom (which broadcasts
            // "TranslationRoomEnded") is never invoked — without this publish the other
            // participants sat in an ended room until they pressed Leave themselves.
            // Published after SaveChangesAsync so a client that refetches on the event cannot
            // observe the room still IN_PROGRESS. Failure to notify must not fail the end
            // itself: the room is already ENDED and persisted by this point.
            try
            {
                if (_redisStateRepository is null)
                {
                    return Result.Success();
                }

                var endedPayload = JsonSerializer.Serialize(new
                {
                    Command = "RoomEnded",
                    RoomId = translationRoomId.ToString()
                });
                await _redisStateRepository.PublishAsync(GatewayCommandsChannel, endedPayload);
            }
            catch (Exception publishEx)
            {
                _logger.LogError(
                    publishEx,
                    "Failed to publish RoomEnded for RoomId: {RoomId}. Room is ended; connected clients will not be redirected automatically.",
                    translationRoomId);
            }

            // WT-67: Trigger Audio Routing State Machine
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while ending translation room. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedEndRoom, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UpdateTranslationRoomSettingsAsync(Guid translationRoomId, Guid hostId, UpdateRoomSettingsRequest request, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

            if (translationRoom == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (!translationRoom.IsHostedBy(hostId))
                return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);

            if (translationRoom.Status != "SCHEDULED" && translationRoom.Status != "WAITING")
                return Result.Failure(TranslationRoomConstants.ErrorSettingsLocked, ErrorCodes.InvalidState);

            // ArtifactAccess is a free-form string in a jsonb blob, and for its whole life nothing
            // checked what went into it. That is how the guard came to compare against values no
            // writer produced without anyone noticing: an unrecognised level is indistinguishable
            // from HOST_ONLY at read time, so the policy silently denied everyone and looked
            // enforced. Reject it here, at the only door it can come through, so the stored value
            // is always one the guard can actually act on.
            if (request.Settings?.ArtifactAccess is { } requestedArtifactAccess
                && !ArtifactAccessLevels.IsValid(requestedArtifactAccess))
            {
                return Result.Failure(
                    string.Format(
                        TranslationRoomConstants.ValidationArtifactAccessUnsupported,
                        requestedArtifactAccess,
                        string.Join(", ", ArtifactAccessLevels.All)),
                    ErrorCodes.ValidationError);
            }

            if (!string.IsNullOrWhiteSpace(request.Title))
                translationRoom.Title = request.Title;

            if (request.Description != null)
                translationRoom.Description = request.Description;

            if (request.MaxParticipants.HasValue)
                translationRoom.MaxParticipants = request.MaxParticipants.Value;

            if (request.ScheduledAt.HasValue)
                translationRoom.ScheduledAt = request.ScheduledAt.Value;

            // WT-187: only publish if this call actually adds someone. Re-sending the same
            // invitee list is a no-op below, and must stay a no-op on the wire too.
            var invitationsAdded = false;

            if (request.InvitedEmails != null && request.InvitedEmails.Any())
            {
                var meetingLink = $"{_frontendBaseUrl}/room/{translationRoom.TranslationRoomCode}";
                var scheduledTime = translationRoom.ScheduledAt?.ToString("f") ?? "Now";
                var invitationRepo = _unitOfWork.TranslationRoomInvitationRepository;

                var existingInvitations = await invitationRepo.FindAsync(i => i.TranslationRoomId == translationRoom.Id, ct: ct);
                var existingEmails = existingInvitations.Select(i => i.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var email in request.InvitedEmails)
                {
                    if (!existingEmails.Contains(email))
                    {
                        await invitationRepo.AddAsync(new WarpTalk.TranslationRoomService.Domain.Entities.TranslationRoomInvitation
                        {
                            TranslationRoomId = translationRoom.Id,
                            Email = email,
                            Status = "PENDING"
                        }, ct);

                        await _emailService.SendMeetingInvitationAsync(email, "Participant", meetingLink, translationRoom.Title, scheduledTime, ct);
                        await NotifyInvitedUserAsync(email, translationRoom, meetingLink, ct);
                        invitationsAdded = true;
                    }
                }
            }

            // WT-65: Update and Validate Source Language
            if (!string.IsNullOrWhiteSpace(request.SourceLanguage))
            {
                var normSourceLang = LanguageHelper.NormalizeLanguageCode(request.SourceLanguage);
                if (!await _languagePolicy.IsSupportedAsync(normSourceLang))
                    return Result.Failure(TranslationRoomConstants.ValidationSourceLanguageUnsupported, ErrorCodes.ValidationError);

                translationRoom.SourceLanguage = normSourceLang;
            }

            // WT-65: Update and Validate Target Languages
            if (request.TargetLanguages != null && request.TargetLanguages.Count > 0)
            {
                var normTargetLangs = request.TargetLanguages.Select(LanguageHelper.NormalizeLanguageCode).ToList();
                foreach (var lang in normTargetLangs)
                {
                    if (!await _languagePolicy.IsSupportedAsync(lang))
                        return Result.Failure(string.Format(TranslationRoomConstants.ValidationLanguageUnsupported, lang), ErrorCodes.ValidationError);
                }

                translationRoom.TargetLanguages = LanguageHelper.SerializeTargetLanguages(normTargetLangs);
            }

            // Update settings. Every field is nullable, so this is a PATCH: an omitted field
            // keeps whatever the room already has rather than being reset to a default. Before
            // the meeting type seeded anything this distinction did not matter; now it does,
            // because resetting would silently undo the type's profile on any unrelated edit.
            if (request.Settings != null)
            {
                var current = System.Text.Json.JsonSerializer.Deserialize<TranslationRoomSettings>(
                    string.IsNullOrEmpty(translationRoom.Settings) ? "{}" : translationRoom.Settings,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new TranslationRoomSettings();

                current.RequiresApproval = request.Settings.RequiresApproval ?? current.RequiresApproval;
                current.ArtifactAccess = request.Settings.ArtifactAccess ?? current.ArtifactAccess;
                current.MuteOnEntry = request.Settings.MuteOnEntry ?? current.MuteOnEntry;
                current.AutoRecord = request.Settings.AutoRecord ?? current.AutoRecord;
                current.BreakoutsEnabled = request.Settings.BreakoutsEnabled ?? current.BreakoutsEnabled;

                translationRoom.Settings = System.Text.Json.JsonSerializer.Serialize(current);
            }

            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            // WT-187: after the commit, and only for a call that really added invitees. This is
            // the case the ticket describes literally — being invited to a room that already
            // exists, where nothing previously told the invitee's client anything at all.
            if (invitationsAdded)
                await PublishRoomInvitationsChangedAsync(translationRoom);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating translation room settings. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedUpdateRoomSettings, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomHistoryResponse>> GetTranslationRoomHistoryAsync(GetTranslationRoomsRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default)
    {
        if (!request.WorkspaceId.HasValue || request.WorkspaceId.Value == Guid.Empty)
        {
            return Result.Failure<TranslationRoomHistoryResponse>(
                "WorkspaceId is required when loading room history.",
                ErrorCodes.ValidationError);
        }

        try
        {
            var historyRequest = request with { Status = request.Status ?? "ENDED,CANCELLED" };

            return Result.Success(await BuildRoomTimelinePageAsync(
                historyRequest, userId, userEmail, RoomTimelineOrder.EndedFirst, RoomTimelineScope.Workspace, ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while loading translation room history for UserId: {UserId}", userId);
            return Result.Failure<TranslationRoomHistoryResponse>("An unexpected error occurred while loading room history.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// WT-333 — UC 25. One user's own meetings inside one workspace, past and upcoming on a single
    /// timeline.
    ///
    /// Deliberately NOT a new query. This is <see cref="GetTranslationRoomHistoryAsync"/> with three
    /// substitutions, and all three are what separates an archive from a diary:
    ///
    /// SCOPE — forced to <c>mine</c>, so a workspace Owner/Admin gets their own rooms instead of the
    /// tenant's. That is the entire bug: the Owner/Admin widening in
    /// <see cref="BuildListableRoomsQueryAsync"/> happens before any filter runs, so there was no
    /// request an Owner could send that meant "only mine".
    ///
    /// STATUS — defaults to the lifecycle states the My Meetings page buckets into upcoming, live,
    /// and past. The room still has to survive <c>DeletedAt == null &amp;&amp; IsActive</c> like everywhere
    /// else.
    ///
    /// ORDER — by ScheduledAt first. A future room has neither EndedAt nor StartedAt, so the
    /// archive's ordering falls through to CreatedAt and sorts upcoming meetings by the day somebody
    /// booked them rather than the day they happen.
    ///
    /// WorkspaceId stays required. A timeline spanning every workspace was considered and dropped:
    /// it would mean taking the tenant boundary off this read for every caller, not just this one.
    /// </summary>
    public async Task<Result<TranslationRoomHistoryResponse>> GetMyMeetingsAsync(GetTranslationRoomsRequest request, Guid userId, string? userEmail = null, CancellationToken ct = default)
    {
        if (!request.WorkspaceId.HasValue || request.WorkspaceId.Value == Guid.Empty)
        {
            return Result.Failure<TranslationRoomHistoryResponse>(
                "WorkspaceId is required when loading my meetings.",
                ErrorCodes.ValidationError);
        }

        try
        {
            var timelineRequest = request with { Status = request.Status ?? BuildMyMeetingsDefaultStatusFilter() };

            return Result.Success(await BuildRoomTimelinePageAsync(
                timelineRequest, userId, userEmail, RoomTimelineOrder.ScheduledFirst, RoomTimelineScope.Mine, ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while loading my meetings for UserId: {UserId}", userId);
            return Result.Failure<TranslationRoomHistoryResponse>("An unexpected error occurred while loading your meetings.", ErrorCodes.InternalServerError);
        }
    }

    private static string BuildMyMeetingsDefaultStatusFilter()
        => string.Join(',', Enum.GetNames<RoomStatus>());

    /// <summary>
    /// The reading order of a page of rooms. Each value exists because one of the two callers has a
    /// timestamp the other one's rooms do not have.
    /// </summary>
    private enum RoomTimelineOrder
    {
        /// <summary>Most recently finished first — every room in the archive has ended.</summary>
        EndedFirst,

        /// <summary>Newest booked slot first — matching the descending personal timeline query.</summary>
        ScheduledFirst,
    }

    /// <summary>
    /// Which caller boundary the shared timeline loader should apply before any filter runs.
    /// Kept private so WT-333 does not widen the public query contract with an internal switch.
    /// </summary>
    private enum RoomTimelineScope
    {
        Workspace,
        Mine,
    }

    /// <summary>
    /// One page of rooms with their roster and artifacts, shared by the workspace archive
    /// (<see cref="GetTranslationRoomHistoryAsync"/>) and the personal timeline
    /// (<see cref="GetMyMeetingsAsync"/>).
    ///
    /// Shared as a body rather than copied: the artifact half below is a per-room authorization
    /// decision, and a second copy of it would be a second place for the ArtifactAccess policy to
    /// drift out of agreement with the download endpoint — which is precisely the WT-304 bug.
    ///
    /// Throws rather than returning a Result: both callers already wrap this in the try/catch that
    /// owns their error message.
    /// </summary>
    private async Task<TranslationRoomHistoryResponse> BuildRoomTimelinePageAsync(
        GetTranslationRoomsRequest request,
        Guid userId,
        string? userEmail,
        RoomTimelineOrder order,
        RoomTimelineScope scope,
        CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var query = ApplyRoomFilters(
                await BuildListableRoomsQueryAsync(userId, userEmail, request.WorkspaceId, ct, scope),
                request)
            .Where(r => r.DeletedAt == null && r.IsActive);

        var total = await query.CountAsync(ct);

        var ordered = order == RoomTimelineOrder.ScheduledFirst
            ? query.OrderByDescending(r => r.ScheduledAt ?? r.StartedAt ?? r.EndedAt ?? r.CreatedAt)
            : query.OrderByDescending(r => r.EndedAt ?? r.StartedAt ?? r.CreatedAt);

        var roomEntities = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var roomIds = roomEntities.Select(r => r.Id).ToList();

        var participantEntities = await _unitOfWork.TranslationRoomParticipantRepository
            .Query()
            .Where(p => roomIds.Contains(p.TranslationRoomId))
            .ToListAsync(ct);
        var participantsByRoom = participantEntities
            .GroupBy(p => p.TranslationRoomId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.ToDto()).ToList());

        // WT-280: history is the one path that already has the full roster in memory, so the
        // seat count comes from it rather than from a second database round trip.
        // HoldsSeat(...) — the METHOD form — is correct here precisely because these rows are
        // already materialised; it must never appear inside a query EF has to translate.
        var occupancyByRoom = participantEntities
            .GroupBy(p => p.TranslationRoomId)
            .ToDictionary(
                g => g.Key,
                g => g.Count(p => TranslationRoomParticipantStatuses.HoldsSeat(p.Status)));

        var artifactEntities = await _unitOfWork.TranslationRoomArtifactRepository
            .Query()
            .Where(a => roomIds.Contains(a.TranslationRoomId) && a.DeletedAt == null)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        // Artifact BODIES are decided per room by the room's own ArtifactAccess policy, not by
        // the room-read gate that got this caller onto the page. History is reachable by every
        // participant and by anyone holding an unaccepted invitation, so a HOST_ONLY room used
        // to hand all of them its AI summary here while the download endpoint refused it.
        // Participation is read from the roster already materialised above rather than from the
        // (unloaded) navigation on each room entity.
        var roomsById = roomEntities.ToDictionary(r => r.Id);
        var participantUserIdsByRoom = participantEntities
            .GroupBy(p => p.TranslationRoomId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.UserId).ToHashSet());

        var artifactsByRoom = artifactEntities
            .GroupBy(a => a.TranslationRoomId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var room = roomsById[g.Key];
                    var includeContent = ArtifactAccessHelper.HasAccessToRoomArtifacts(
                        room.HostId,
                        room.Settings,
                        participantUserIdsByRoom.GetValueOrDefault(g.Key)?.Contains(userId) == true,
                        userId);

                    return g.Select(a => ToArtifactDto(a, includeContent)).ToList();
                });

        var rooms = roomEntities.Select(room => new TranslationRoomHistoryItemDto(
                ToListItemDto(room, userId, occupancyByRoom.GetValueOrDefault(room.Id)),
                participantsByRoom.GetValueOrDefault(room.Id, new List<TranslationRoomParticipantDto>()),
                artifactsByRoom.GetValueOrDefault(room.Id, new List<TranslationRoomArtifactDto>())
            ))
            .ToList();

        return new TranslationRoomHistoryResponse(rooms, total, page, pageSize);
    }

    public async Task<Result<List<TranslationRoomArtifactDto>>> GetTranslationRoomArtifactsAsync(Guid translationRoomId, Guid userId, string? userEmail = null, CancellationToken ct = default)
    {
        try
        {
            if (!await CanAccessRoomAsync(translationRoomId, userId, userEmail, ct))
                return Result.Failure<List<TranslationRoomArtifactDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // Room-read got the caller this far — that is what entitles them to know which
            // artifacts exist. Whether they may also read an artifact's inline body is the
            // stricter, policy-driven question the download endpoint asks, so ask it with the
            // same predicate rather than letting the list be the looser of the two.
            var room = await _unitOfWork.TranslationRoomRepository.FirstOrDefaultAsync(
                r => r.Id == translationRoomId,
                "TranslationRoomParticipants",
                ct);
            var includeContent = room != null && ArtifactAccessHelper.HasAccessToRoomArtifacts(room, userId);

            var artifactEntities = await _unitOfWork.TranslationRoomArtifactRepository
                .Query()
                .Where(a => a.TranslationRoomId == translationRoomId && a.DeletedAt == null)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(ct);
            var artifacts = artifactEntities.Select(a => ToArtifactDto(a, includeContent)).ToList();

            return Result.Success(artifacts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while loading artifacts. RoomId: {RoomId}, UserId: {UserId}", translationRoomId, userId);
            return Result.Failure<List<TranslationRoomArtifactDto>>("An unexpected error occurred while loading artifacts.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomFeedbackStateDto>> GetFeedbackStateAsync(Guid translationRoomId, Guid userId, string? userEmail = null, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null || !await CanAccessRoomAsync(translationRoomId, userId, userEmail, ct))
                return Result.Failure<TranslationRoomFeedbackStateDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (room.Status != "ENDED")
                return Result.Failure<TranslationRoomFeedbackStateDto>("Feedback is only available after a room ends.", ErrorCodes.InvalidState);

            var feedback = await _unitOfWork.TranslationRoomFeedbackRepository
                .FirstOrDefaultAsync(f => f.TranslationRoomId == translationRoomId && f.UserId == userId, ct: ct);

            return Result.Success(new TranslationRoomFeedbackStateDto(feedback != null, feedback != null ? ToFeedbackDto(feedback) : null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while loading feedback state. RoomId: {RoomId}, UserId: {UserId}", translationRoomId, userId);
            return Result.Failure<TranslationRoomFeedbackStateDto>("An unexpected error occurred while loading feedback.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomFeedbackDto>> SubmitFeedbackAsync(Guid translationRoomId, Guid userId, SubmitTranslationRoomFeedbackRequest request, string? userEmail = null, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null || !await CanAccessRoomAsync(translationRoomId, userId, userEmail, ct))
                return Result.Failure<TranslationRoomFeedbackDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (room.Status != "ENDED")
                return Result.Failure<TranslationRoomFeedbackDto>("Feedback is only available after a room ends.", ErrorCodes.InvalidState);

            var feedbackRepository = _unitOfWork.TranslationRoomFeedbackRepository;
            var existing = await feedbackRepository.FirstOrDefaultAsync(f => f.TranslationRoomId == translationRoomId && f.UserId == userId, ct: ct);
            if (existing != null)
                return Result.Failure<TranslationRoomFeedbackDto>("Feedback has already been submitted for this room.", ErrorCodes.InvalidState);

            var feedback = new TranslationRoomFeedback
            {
                Id = Guid.CreateVersion7(),
                TranslationRoomId = translationRoomId,
                UserId = userId,
                OverallRating = request.OverallRating,
                TranslationQuality = request.TranslationQuality,
                AudioQuality = request.AudioQuality,
                VoiceCloneQuality = request.VoiceCloneQuality,
                AiSummaryQuality = request.AiSummaryQuality,
                Comments = string.IsNullOrWhiteSpace(request.Comments) ? null : request.Comments.Trim(),
                CommunicationInsights = request.CommunicationInsights == null ? null : JsonSerializer.Serialize(request.CommunicationInsights),
                CreatedAt = DateTime.UtcNow
            };

            await feedbackRepository.AddAsync(feedback, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(ToFeedbackDto(feedback));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while submitting feedback. RoomId: {RoomId}, UserId: {UserId}", translationRoomId, userId);
            return Result.Failure<TranslationRoomFeedbackDto>("An unexpected error occurred while submitting feedback.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<string>> GenerateCalendarIcsAsync(Guid translationRoomId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
                return Result.Failure<string>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (!room.ScheduledAt.HasValue)
                return Result.Failure<string>(TranslationRoomConstants.ErrorRoomNotScheduled, ErrorCodes.InvalidState);

            var joinLink = $"{_frontendBaseUrl}/room/{room.TranslationRoomCode}";
            var ics = IcsCalendarBuilder.Build(
                uid: $"{room.Id}@warptalk.vn",
                title: room.Title,
                description: room.Description,
                scheduledAtUtc: room.ScheduledAt.Value,
                joinLink: joinLink);

            return Result.Success(ics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while generating calendar .ics. RoomId: {RoomId}", translationRoomId);
            return Result.Failure<string>("An unexpected error occurred while generating the calendar invite.", ErrorCodes.InternalServerError);
        }
    }

    private async Task StartNewTranslationSessionAsync(TranslationRoom translationRoom, CancellationToken ct)
    {
        var activeSession = await _translationRoomSessionRepository.GetActiveSessionByRoomIdAsync(translationRoom.Id, ct);
        if (activeSession != null) return;

        var now = DateTime.UtcNow;
        await _translationRoomSessionRepository.AddAsync(new TranslationRoomSession
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = translationRoom.Id,
            MainLanguage = translationRoom.SourceLanguage,
            Status = TranslationRoomSessionStatus.ACTIVE.ToString(),
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        }, ct);
    }

    private async Task EndActiveTranslationSessionAsync(Guid translationRoomId, CancellationToken ct)
    {
        var activeSession = await _translationRoomSessionRepository.GetActiveSessionByRoomIdAsync(translationRoomId, ct);
        if (activeSession == null) return;

        activeSession.Status = TranslationRoomSessionStatus.ENDED.ToString();
        activeSession.EndedAt = DateTime.UtcNow;
        activeSession.UpdatedAt = DateTime.UtcNow;
        _translationRoomSessionRepository.Update(activeSession);
    }

    /// <summary>
    /// WT-304: the clauses now come from <see cref="RoomReadAccess.IsReadableBy"/>, shared with
    /// <see cref="CanAccessRoomAsync"/>. This site is also the one that gained a restriction:
    /// it used to accept an invitation row in ANY state, so a DECLINED invitation still listed the
    /// room. No code writes DECLINED today, so nothing observable changes — but the list and the
    /// artifacts/feedback guard now agree by construction instead of by coincidence.
    /// </summary>
    private IQueryable<TranslationRoom> BuildAccessibleRoomsQuery(Guid userId, string? userEmail)
    {
        return _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(RoomReadAccess.IsReadableBy(userId, userEmail));
    }

    /// <summary>
    /// The rooms a caller may SEE LISTED for one workspace.
    ///
    /// <see cref="BuildAccessibleRoomsQuery"/> knows three ways in — host, prior participant,
    /// invited by email — and a workspace Owner/Admin is none of them, because that is not a fact
    /// the translation-room database holds. So a workspace Admin saw "No active meetings found."
    /// and a dashboard tile reading 0 for a workspace that had rooms in it, while the same account
    /// could open any of those rooms by direct URL and join: the list was stricter than the thing it
    /// was a list of. Since the Join control lives only on the room detail page, and the list is the
    /// only route to it, an empty list left an Admin no way into any meeting in her own workspace.
    ///
    /// WHY OWNER/ADMIN AND NOT EVERY MEMBER. This is the rule WT-313 already ratified for "who may
    /// act on this room" — host OR participant OR workspace Owner/Admin — after the same predicate
    /// had drifted into three different spellings. That work audited
    /// <c>TranslationRoomParticipantService</c> and never reached this file, so the rooms list is
    /// the un-audited next instance of a settled question rather than a new one. WT-313 also keeps a
    /// plain workspace Member as a deliberate NEGATIVE case, so widening to every member would
    /// reverse a decision the team has already taken; a member still sees exactly the rooms they
    /// host, joined, or were invited to.
    ///
    /// The role is asked of WorkspaceService through the same directory WT-313 uses, once per
    /// request, and only when the request names a workspace. Everyone else keeps precisely the
    /// previous answer — as does every caller when WorkspaceService cannot be reached, since the
    /// directory swallows its own failures and returns false, narrowing the list rather than
    /// failing the request.
    ///
    /// HOW FAR THIS WIDENS. For an Owner/Admin, to every non-deleted room of that one workspace,
    /// deliberately: a room has no private/unlisted/visibility attribute to respect —
    /// <c>TranslationRoomTypes</c> selects behaviour (approval, recording, capacity), not audience —
    /// and entry is still governed per-room by <c>RequiresApproval</c>, untouched here. Listing a
    /// room is not admission to it, and artifact bodies stay behind their own per-room
    /// ArtifactAccess policy. If a private room type is ever introduced, its exclusion belongs here.
    ///
    /// WT-333: <paramref name="scope"/> lets the dedicated personal-timeline route DECLINE the
    /// Owner/Admin widening above. The widening is what the workspace archive needs and what a
    /// personal timeline must not have — an Owner opening "My Meetings" was handed every room in
    /// the tenant, with no filter that could take it back, because the widening happens before any
    /// filter runs. Asking for <see cref="RoomTimelineScope.Mine"/> returns the caller to the
    /// ordinary read boundary, the same one every non-Owner already gets. It only ever NARROWS:
    /// no scope value can reach a room
    /// <see cref="BuildAccessibleRoomsQuery"/> would refuse, so this is not a new authorization
    /// path and there is no new predicate to keep in sync.
    /// </summary>
    private async Task<IQueryable<TranslationRoom>> BuildListableRoomsQueryAsync(
        Guid userId,
        string? userEmail,
        Guid? workspaceId,
        CancellationToken ct,
        RoomTimelineScope scope = RoomTimelineScope.Workspace)
    {
        if (scope == RoomTimelineScope.Mine)
        {
            return BuildAccessibleRoomsQuery(userId, userEmail);
        }

        if (workspaceId.HasValue
            && workspaceId.Value != Guid.Empty
            && await _workspaceMemberDirectory.IsOwnerOrAdminAsync(workspaceId.Value, userId, ct))
        {
            var scopedWorkspaceId = workspaceId.Value;
            return _unitOfWork.TranslationRoomRepository
                .Query()
                .Where(r => r.WorkspaceId == scopedWorkspaceId);
        }

        return BuildAccessibleRoomsQuery(userId, userEmail);
    }

    private static IQueryable<TranslationRoom> ApplyRoomFilters(IQueryable<TranslationRoom> query, GetTranslationRoomsRequest request)
    {
        if (request.WorkspaceId.HasValue)
        {
            query = query.Where(r => r.WorkspaceId == request.WorkspaceId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var statuses = request.Status
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse<RoomStatus>(s, true, out var parsedStatus) ? parsedStatus : (RoomStatus?)null)
                .Where(s => s.HasValue)
                .Select(s => s!.Value.ToString())
                .ToList();
            query = query.Where(r => statuses.Contains(r.Status));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLowerInvariant();
            query = query.Where(r =>
                r.Title.ToLower().Contains(search) ||
                r.TranslationRoomCode.ToLower().Contains(search) ||
                (r.Description != null && r.Description.ToLower().Contains(search)));
        }

        if (request.From.HasValue)
            query = query.Where(r => (r.ScheduledAt ?? r.StartedAt ?? r.CreatedAt) >= request.From.Value);

        if (request.To.HasValue)
            query = query.Where(r => (r.ScheduledAt ?? r.StartedAt ?? r.CreatedAt) <= request.To.Value);

        return query;
    }

    /// <summary>
    /// WT-304/WT-330(e): this guard was missing the invitation clause that
    /// <see cref="BuildAccessibleRoomsQuery"/> has always had, so a user who could see a room in
    /// their list — because they were invited by email — was refused its artifacts, and (silently,
    /// unreported) its feedback too. Both now resolve through
    /// <see cref="RoomReadAccess.IsReadableBy"/>, the same expression the list uses.
    ///
    /// Still synchronous underneath: the caller-supplied <paramref name="ct"/> was never honoured
    /// here and switching to AnyAsync would require an EF async query provider, which the unit
    /// tests' in-memory IQueryable does not implement. Left alone deliberately — widening the read
    /// boundary and changing its execution model in the same commit is how this predicate got
    /// three different spellings in the first place.
    /// </summary>
    private async Task<bool> CanAccessRoomAsync(Guid translationRoomId, Guid userId, string? userEmail, CancellationToken ct)
    {
        var scoped = _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(r => r.Id == translationRoomId && r.DeletedAt == null && r.IsActive);

        if (scoped.Any(RoomReadAccess.IsReadableBy(userId, userEmail))) return true;

        var workspaceId = scoped.Select(r => r.WorkspaceId).FirstOrDefault();

        // A workspace Owner/Admin can see every room in their workspace in the list
        // (BuildListableRoomsQueryAsync), so the detail read has to agree. Without this the
        // product hands an Admin a list of rooms and then refuses to open them — the mentor
        // incident inverted, and worse on stage, because the presenter has already clicked.
        return workspaceId != Guid.Empty
            && await _workspaceMemberDirectory.IsOwnerOrAdminAsync(workspaceId, userId, ct);
    }

    /// <summary>
    /// WT-280: <paramref name="seatsTaken"/> is supplied by the caller, which has counted CONNECTED
    /// participants in the database. This used to read <c>room.TranslationRoomParticipants.Count</c>,
    /// which was wrong twice over: it counted every row whatever its status (LEFT, KICKED, REJECTED,
    /// still in the lobby), and — since no list query Includes that navigation — it silently returned
    /// 0, which is how a room with a CONNECTED host rendered as "0/100".
    /// </summary>
    /// <summary>
    /// WT-327: which single room stands in for each series in a grouped list, how many occurrences
    /// it stands for, and when the next one is.
    ///
    /// The representative is the occurrence a user would act on: the next one at or after now, or
    /// the most recent one when the whole series is behind them. Picking the first row by date
    /// would show a standup that started three weeks ago; picking the last would show one a month
    /// out while today's is live.
    ///
    /// Resolved by pulling two scalars per occurrence and grouping in memory, rather than as a
    /// correlated subquery. The set is already narrowed by the caller's own visibility filters and
    /// the status filter, and this keeps the pick — "next, else last" — in code that can be read,
    /// instead of in an ORDER BY whose EF translation is one provider upgrade from silently
    /// changing which room the meetings list points at.
    /// </summary>
    private static async Task<Dictionary<Guid, SeriesGrouping>> ResolveSeriesRepresentativesAsync(
        IQueryable<TranslationRoom> query,
        CancellationToken ct)
    {
        var occurrences = await query
            .Where(r => r.SeriesId != null)
            .Select(r => new
            {
                r.Id,
                SeriesId = r.SeriesId!.Value,
                r.ScheduledAt,
                r.StartedAt,
                r.CreatedAt
            })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var grouped = new Dictionary<Guid, SeriesGrouping>();

        foreach (var series in occurrences.GroupBy(o => o.SeriesId))
        {
            // Same fallback chain the list's own ORDER BY uses, so the row the user sees first and
            // the row the group collapses to are ordered by the same clock.
            var ordered = series
                .Select(o => new { o.Id, When = o.ScheduledAt ?? o.StartedAt ?? o.CreatedAt })
                .OrderBy(o => o.When)
                .ToList();

            var upcoming = ordered.FirstOrDefault(o => o.When >= now);
            var representative = upcoming ?? ordered[^1];

            grouped[series.Key] = new SeriesGrouping(representative.Id, ordered.Count, upcoming?.When);
        }

        return grouped;
    }

    /// <summary>
    /// The rule behind each collapsed row on this page, read in one query rather than one per row.
    /// </summary>
    private async Task<Dictionary<Guid, SeriesListSummaryDto>> BuildSeriesSummariesAsync(
        List<TranslationRoom> pageRooms,
        Dictionary<Guid, SeriesGrouping> grouping,
        CancellationToken ct)
    {
        var seriesIds = pageRooms
            .Where(r => r.SeriesId.HasValue)
            .Select(r => r.SeriesId!.Value)
            .Distinct()
            .ToList();

        if (seriesIds.Count == 0) return new Dictionary<Guid, SeriesListSummaryDto>();

        var series = await _unitOfWork.TranslationRoomSeriesRepository
            .FindAsync(s => seriesIds.Contains(s.Id), ct: ct);

        return series.ToDictionary(
            s => s.Id,
            s =>
            {
                var counts = grouping.GetValueOrDefault(s.Id);
                return new SeriesListSummaryDto(
                    s.Id,
                    s.RecurrenceType,
                    s.RecurrenceInterval,
                    RecurrenceRuleJson.ReadWeekdays(s.RecurrenceByWeekdays)?.ToList(),
                    s.RecurrenceByMonthDay,
                    s.StartTimeLocal.ToString("HH:mm", CultureInfo.InvariantCulture),
                    s.TimeZone,
                    s.Status,
                    counts?.OccurrenceCount ?? 0,
                    counts?.NextOccurrenceAt);
            });
    }

    /// <summary>WT-327: one series' place in a grouped list.</summary>
    private sealed record SeriesGrouping(Guid RepresentativeRoomId, int OccurrenceCount, DateTime? NextOccurrenceAt);

    private static TranslationRoomListItemDto ToListItemDto(
        TranslationRoom room,
        Guid userId,
        int seatsTaken,
        SeriesListSummaryDto? series = null,
        // Distinct people who have ever been in the room. Trailing and defaulted so the sites
        // that have not been taught to fetch it keep their previous behaviour rather than
        // reporting a fabricated 0 as if it were an answer.
        int attendedCount = 0)
    {
        // Same reader the detail endpoints use — the list used to deserialize the snake_case
        // blob straight into the PascalCase response record (without even
        // PropertyNameCaseInsensitive), so every room in the list reported default settings.
        var settings = TranslationRoomMapper.ReadSettings(room.Settings);

        return new TranslationRoomListItemDto(
            room.Id,
            room.WorkspaceId,
            room.HostId,
            room.Title,
            room.Description,
            room.TranslationRoomCode,
            Enum.TryParse<RoomStatus>(room.Status, true, out var parsedStatus) ? parsedStatus : RoomStatus.SCHEDULED,
            room.TranslationRoomType,
            room.MaxParticipants,
            room.SourceLanguage,
            LanguageHelper.ParseTargetLanguages(room.TargetLanguages),
            room.ScheduledAt,
            null, // InvitedEmails
            room.StartedAt,
            room.EndedAt,
            room.DurationSeconds,
            room.CreatedAt,
            settings,
            seatsTaken,
            // WT-353: the effective host. The list said "you are the host" to whoever booked the
            // room, so after a transfer the old host still saw host controls and the new one did not.
            room.IsHostedBy(userId),
            room.SeriesId,
            series,
            attendedCount
        );
    }

    /// <summary>
    /// The artifact list projection.
    /// </summary>
    /// <param name="includeContent">
    /// Whether the caller is entitled to the artifact's inline BODY, as opposed to the fact that it
    /// exists. These two lists — the room's artifacts and the per-room artifacts inside room history
    /// — are guarded only by <c>CanAccessRoomAsync</c>, which is room-READ: it admits every
    /// participant, and (via <c>RoomReadAccess</c>) anyone holding an unaccepted email invitation.
    /// <c>Content</c> is where the AI meeting summary lives — overview, decisions, action items —
    /// so shipping it unconditionally handed the entire summary to exactly the people a
    /// <c>HOST_ONLY</c> room is meant to withhold it from. The download endpoint refused them, which
    /// is precisely why the policy looked like it was working. Metadata still goes to everyone who
    /// may see the room: they should know a summary exists and may ask the host for it.
    /// The caller decides entitlement with <see cref="ArtifactAccessHelper"/> — the same predicate
    /// the download endpoint uses, so the two cannot drift apart again.
    /// </param>
    private static TranslationRoomArtifactDto ToArtifactDto(TranslationRoomArtifact artifact, bool includeContent)
    {
        // WT-13: previously this ignored artifact.ArtifactType entirely and derived `type`
        // only from FileFormat, so every artifact (summary, recording, transcript alike)
        // came back as "TRANSCRIPT_EXPORT" unless FileFormat happened to be "debug" — which
        // silently broke any client-side filtering by artifact type (e.g. the AI summaries
        // page looking for "summary_export"). Use the actual stored ArtifactType, falling
        // back to the old heuristic only if it's somehow missing.
        var type = !string.IsNullOrWhiteSpace(artifact.ArtifactType)
            ? artifact.ArtifactType
            : artifact.FileFormat?.Equals("debug", StringComparison.OrdinalIgnoreCase) == true
                ? "DEBUG_LOG"
                : "TRANSCRIPT_EXPORT";

        return new TranslationRoomArtifactDto(
            artifact.Id,
            artifact.TranslationRoomId,
            type,
            BuildArtifactTitle(type, artifact.FileFormat),
            artifact.FileUrl,
            artifact.FileFormat,
            artifact.FileSizeBytes,
            artifact.ContainsRawAudio,
            artifact.ContainsRawVideo,
            artifact.ConsentRequired,
            artifact.RetentionUntil,
            artifact.Status,
            artifact.CreatedAt,
            includeContent ? artifact.Content : null
        );
    }

    private static string BuildArtifactTitle(string type, string? format)
    {
        var label = type.ToLowerInvariant().Replace('_', ' ');
        return string.IsNullOrWhiteSpace(format) ? label : $"{label} ({format.ToUpperInvariant()})";
    }

    private static TranslationRoomFeedbackDto ToFeedbackDto(TranslationRoomFeedback feedback)
    {
        Dictionary<string, object>? insights = null;
        if (!string.IsNullOrWhiteSpace(feedback.CommunicationInsights))
        {
            try
            {
                insights = JsonSerializer.Deserialize<Dictionary<string, object>>(feedback.CommunicationInsights);
            }
            catch
            {
                insights = new Dictionary<string, object> { ["raw"] = feedback.CommunicationInsights };
            }
        }

        return new TranslationRoomFeedbackDto(
            feedback.Id,
            feedback.TranslationRoomId,
            feedback.UserId,
            feedback.OverallRating,
            feedback.TranslationQuality,
            feedback.AudioQuality,
            feedback.VoiceCloneQuality,
            feedback.AiSummaryQuality,
            feedback.Comments,
            insights,
            feedback.CreatedAt
        );
    }

}
