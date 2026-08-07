using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Domain.Configuration;
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
    private readonly WarpTalk.Shared.Interfaces.IEmailService _emailService;
    private readonly IRedisStateRepository? _redisStateRepository;
    private readonly ILogger<TranslationRoomService> _logger;
    private readonly string _frontendBaseUrl;

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

    public TranslationRoomService(
        IUnitOfWork unitOfWork,
        ILanguagePolicy languagePolicy,
        IAudioRouteEventProcessor audioRouteEventProcessor,
        ITranslationRoomAudioRouteService audioRouteService,
        IUserSettingsDirectory userSettingsDirectory,
        IWorkspaceMeetingPolicy workspaceMeetingPolicy,
        WarpTalk.Shared.Interfaces.IEmailService emailService,
        ILogger<TranslationRoomService> logger,
        IOptions<AppSettings>? appSettings = null,
        IRedisStateRepository? redisStateRepository = null)
    {
        _unitOfWork = unitOfWork;
        _languagePolicy = languagePolicy;
        _audioRouteEventProcessor = audioRouteEventProcessor;
        _audioRouteService = audioRouteService;
        _userSettingsDirectory = userSettingsDirectory;
        _workspaceMeetingPolicy = workspaceMeetingPolicy;
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

            // 2. Generate unique 12-char alphanumeric TranslationRoomCode
            string roomCode;
            bool exists;
            do
            {
                roomCode = RoomCodeGenerator.GenerateCode();
                exists = await _translationRoomRepository.ExistsByCodeAsync(roomCode, TranslationRoomConstants.TerminalStatuses, ct);
            } while (exists);

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
                room.Id, hostId, hostDisplayName, sourceLang, targetLangs);
            await _participantRepository.AddAsync(hostParticipant, ct);

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

                    // 2. Send the email
                    if (sendInvitationEmails)
                    {
                        emailTasks.Add(_emailService.SendMeetingInvitationAsync(email, "Participant", meetingLink, request.Title, scheduledTime, ct));
                    }
                }

                // Save the newly added invitations
                await _unitOfWork.SaveChangesAsync(ct);

                // WT-187: published after the invitations are committed, so a client that
                // refetches the moment it receives the event cannot miss them.
                await PublishRoomInvitationsChangedAsync(room);
                // Send all emails in parallel
                await Task.WhenAll(emailTasks);
            }

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

    public async Task<Result<TranslationRoomDto>> GetTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

            if (translationRoom == null)
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
            var query = BuildAccessibleRoomsQuery(userId, userEmail)
                .Where(r => r.DeletedAt == null && r.IsActive);

            var activeRequest = request with { Status = request.Status ?? "SCHEDULED,WAITING,IN_PROGRESS,PAUSED" };
            query = ApplyRoomFilters(query, activeRequest);

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
            var occupancyByRoom = await _participantRepository.CountSeatHoldingParticipantsByRoomsAsync(
                roomEntities.Select(r => r.Id).ToList(),
                ct);

            var rooms = roomEntities
                .Select(r => ToListItemDto(r, userId, occupancyByRoom.GetValueOrDefault(r.Id)))
                .ToList();

            return Result.Success(new TranslationRoomListResponse(rooms, total, page, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing translation rooms for UserId: {UserId}", userId);
            return Result.Failure<TranslationRoomListResponse>("An unexpected error occurred while listing rooms.", ErrorCodes.InternalServerError);
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

            var isHost = translationRoom.HostId == userId;

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

    public async Task<Result> OpenWaitingRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);

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

    public async Task<Result<TranslationRoomDto>> StartTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

            if (translationRoom == null)
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (translationRoom.HostId != hostId)
                return Result.Failure<TranslationRoomDto>("Only the host can start the room.", ErrorCodes.Forbidden);

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

                return Result.Success(translationRoom.ToResponseDto(
                    await _participantRepository.CountSeatHoldingParticipantsAsync(translationRoom.Id, ct)));
            }

            if (translationRoom.Status != "SCHEDULED" && translationRoom.Status != "WAITING")
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorInvalidTransitionToStart, ErrorCodes.InvalidState);

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
            translationRoom.UpdatedBy = hostId;

            _translationRoomRepository.Update(translationRoom);

            // Each Start/Resume opens a new numbered translation session — the transcript
            // labels segments by which session they fall in ("Translation 1", "Translation 2"...).
            await StartNewTranslationSessionAsync(translationRoom, ct);

            await _unitOfWork.SaveChangesAsync(ct);
            await PublishRoomTargetLanguagesAsync(translationRoom, ct);

            // WT-322: tell everyone already in the room that translation is now live. Published
            // after SaveChangesAsync for the same reason RoomEnded is: a client that refetches on
            // the event must not be able to observe the room still WAITING. Failure to notify must
            // not fail the start — the room is IN_PROGRESS and persisted by this point.
            await PublishRoomStartedAsync(translationRoom, ct);

            // Trigger Audio Routing State Machine (Transition routes from ROUTING_READY to AUDIO_ROUTING_ACTIVE)
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.session_starts.ToString(), "{}", ct);

            return Result.Success(translationRoom.ToResponseDto(
                await _participantRepository.CountSeatHoldingParticipantsAsync(translationRoom.Id, ct)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while starting translation room. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while starting the room.", ErrorCodes.InternalServerError);
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
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);

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
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);

            if (translationRoom.Status != "PAUSED")
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToInProgress, ErrorCodes.InvalidState);

            translationRoom.Status = "IN_PROGRESS";
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);

            // See StartTranslationRoomAsync — Resume opens a new numbered session too.
            await StartNewTranslationSessionAsync(translationRoom, ct);

            await _unitOfWork.SaveChangesAsync(ct);

            // WT-67: Trigger Audio Routing State Machine to Resume
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.room_resume.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming translation room. RoomId: {RoomId}", translationRoomId);
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

            if (translationRoom.HostId != hostId)
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

            if (translationRoom.HostId != hostId)
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

            if (translationRoom.HostId != hostId)
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
            var page = Math.Max(1, request.Page);
            var pageSize = Math.Clamp(request.PageSize, 1, 100);
            var historyRequest = request with { Status = request.Status ?? $"{"ENDED"},{"CANCELLED"}" };
            var query = ApplyRoomFilters(BuildAccessibleRoomsQuery(userId, userEmail), historyRequest)
                .Where(r => r.DeletedAt == null && r.IsActive);

            var total = await query.CountAsync(ct);

            var roomEntities = await query
                .OrderByDescending(r => r.EndedAt ?? r.StartedAt ?? r.CreatedAt)
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

            return Result.Success(new TranslationRoomHistoryResponse(rooms, total, page, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while loading translation room history for UserId: {UserId}", userId);
            return Result.Failure<TranslationRoomHistoryResponse>("An unexpected error occurred while loading room history.", ErrorCodes.InternalServerError);
        }
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
    private Task<bool> CanAccessRoomAsync(Guid translationRoomId, Guid userId, string? userEmail, CancellationToken ct)
    {
        return Task.FromResult(_unitOfWork.TranslationRoomRepository
            .Query()
            .Where(r => r.Id == translationRoomId && r.DeletedAt == null && r.IsActive)
            .Any(RoomReadAccess.IsReadableBy(userId, userEmail)));
    }

    /// <summary>
    /// WT-280: <paramref name="seatsTaken"/> is supplied by the caller, which has counted CONNECTED
    /// participants in the database. This used to read <c>room.TranslationRoomParticipants.Count</c>,
    /// which was wrong twice over: it counted every row whatever its status (LEFT, KICKED, REJECTED,
    /// still in the lobby), and — since no list query Includes that navigation — it silently returned
    /// 0, which is how a room with a CONNECTED host rendered as "0/100".
    /// </summary>
    private static TranslationRoomListItemDto ToListItemDto(TranslationRoom room, Guid userId, int seatsTaken)
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
            room.HostId == userId,
            room.SeriesId
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
