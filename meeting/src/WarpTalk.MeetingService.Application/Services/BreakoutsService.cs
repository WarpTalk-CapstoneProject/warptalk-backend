using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Services;

/// <summary>
/// WT-Breakout (scoped-down): host creates N groups, each backed by its own LiveKit
/// provider room (reusing ILiveKitTokenService.GenerateToken exactly like
/// MeetingRoomService.JoinMeetingAsync does for the main room — see BreakoutSession's
/// ProviderRoomName). Out of scope per the ticket: per-breakout AI translation pipeline
/// coordination (ActiveTranslationRoomRegistry/STT/translation workers only know about the
/// PARENT room — breakout audio is plain LiveKit A/V with no live captions/translation),
/// cross-breakout broadcast messaging, per-breakout chat history, and breakout recording.
/// Timed sessions are expired by the Meeting Service background worker through
/// ExpireDueBreakoutsAsync.
/// </summary>
public class BreakoutsService : IBreakoutsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomGrpcService _grpcService;
    private readonly IRedisService _redisService;
    private readonly ILiveKitTokenService _tokenService;

    // Same relay channel MeetingRoomService.PublishGatewayCommandAsync/PollsService use —
    // the Gateway's TranslationRoomRedisSubscriberService forwards it into TranslationRoomHub.
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";

    private static readonly JsonSerializerOptions RelayJsonOptions = new(JsonSerializerOptions.Default)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BreakoutsService(
        IUnitOfWork unitOfWork,
        ITranslationRoomGrpcService grpcService,
        IRedisService redisService,
        ILiveKitTokenService tokenService)
    {
        _unitOfWork = unitOfWork;
        _grpcService = grpcService;
        _redisService = redisService;
        _tokenService = tokenService;
    }

    public async Task<Result<CreateBreakoutsResponse>> StartBreakoutsAsync(Guid translationRoomId, Guid callerUserId, CreateBreakoutsRequest request, CancellationToken ct = default)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<CreateBreakoutsResponse>("Meeting room not found.", ErrorCodes.NotFound);

        if (!await IsHostAsync(translationRoomId, meetingRoom, callerUserId))
            return Result.Failure<CreateBreakoutsResponse>("Only the host can start breakout rooms.", ErrorCodes.Forbidden);

        var groups = request.Groups ?? new List<BreakoutGroupRequest>();
        if (groups.Count == 0)
            return Result.Failure<CreateBreakoutsResponse>("At least one group is required.", ErrorCodes.ValidationError);

        if (request.DurationSeconds.HasValue && request.DurationSeconds.Value <= 0)
            return Result.Failure<CreateBreakoutsResponse>("Duration must be positive.", ErrorCodes.ValidationError);

        // A user assigned to more than one group is ambiguous (which sub-room's token would
        // GetMyAssignmentAsync mint?) — reject rather than silently picking one.
        var allUserIds = groups.SelectMany(g => g.UserIds ?? new List<Guid>()).ToList();
        if (allUserIds.Count != allUserIds.Distinct().Count())
            return Result.Failure<CreateBreakoutsResponse>("A participant cannot be assigned to more than one group.", ErrorCodes.ValidationError);

        if (allUserIds.Count == 0)
            return Result.Failure<CreateBreakoutsResponse>("At least one participant must be assigned to a group.", ErrorCodes.ValidationError);

        // Restarting breakouts (host clicks "Start" again) implicitly replaces any still-open
        // ones for this room rather than layering a second set on top.
        var previousActive = await _unitOfWork.BreakoutSessionRepository
            .FindAsync(s => s.ParentMeetingRoomId == meetingRoom.Id && s.EndedAt == null, ct: ct);
        var now = DateTime.UtcNow;
        foreach (var previous in previousActive)
        {
            previous.EndedAt = now;
            _unitOfWork.BreakoutSessionRepository.Update(previous);
        }

        var sessions = new List<BreakoutSession>();
        var assignments = new List<BreakoutAssignment>();
        var relayAssignments = new List<BreakoutAssignmentRelayDto>();
        var responseSessions = new List<BreakoutSessionDto>();

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var label = string.IsNullOrWhiteSpace(group.Label) ? $"Group {i + 1}" : group.Label!.Trim();
            var providerRoomName = $"{meetingRoom.ProviderRoomName}-breakout-{i + 1}";

            var session = new BreakoutSession
            {
                Id = Guid.NewGuid(),
                ParentMeetingRoomId = meetingRoom.Id,
                ProviderRoomName = providerRoomName,
                Label = label,
                DurationSeconds = request.DurationSeconds,
                StartedAt = now,
                CreatedAt = now
            };
            sessions.Add(session);
            await _unitOfWork.BreakoutSessionRepository.AddAsync(session, ct);

            var userIds = (group.UserIds ?? new List<Guid>()).Distinct().ToList();
            foreach (var userId in userIds)
            {
                var assignment = new BreakoutAssignment
                {
                    Id = Guid.NewGuid(),
                    BreakoutSessionId = session.Id,
                    UserId = userId,
                    CreatedAt = now
                };
                assignments.Add(assignment);
                await _unitOfWork.BreakoutAssignmentRepository.AddAsync(assignment, ct);
                relayAssignments.Add(new BreakoutAssignmentRelayDto { UserId = userId, SessionId = session.Id, Label = label });
            }

            responseSessions.Add(new BreakoutSessionDto
            {
                Id = session.Id,
                Label = label,
                ProviderRoomName = providerRoomName,
                UserIds = userIds
            });
        }

        await _unitOfWork.SaveChangesAsync(ct);

        await PublishRelayAsync("BreakoutsStarted", translationRoomId, new
        {
            Assignments = ToRelayJson(relayAssignments),
            DurationSeconds = request.DurationSeconds,
            StartedAt = now.ToString("o")
        });

        return Result.Success(new CreateBreakoutsResponse
        {
            Sessions = responseSessions,
            DurationSeconds = request.DurationSeconds,
            StartedAt = now
        });
    }

    /// <summary>
    /// Ends every still-open breakout session for the room and relays BreakoutsEnded so every
    /// client (assigned or not) reconciles back to the main room.
    /// Timed sessions are also ended independently by BreakoutExpiryWorker.
    /// </summary>
    public async Task<Result<bool>> EndBreakoutsAsync(Guid translationRoomId, Guid callerUserId, CancellationToken ct = default)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<bool>("Meeting room not found.", ErrorCodes.NotFound);

        if (!await IsHostAsync(translationRoomId, meetingRoom, callerUserId))
            return Result.Failure<bool>("Only the host can end breakout rooms.", ErrorCodes.Forbidden);

        var activeSessions = await _unitOfWork.BreakoutSessionRepository
            .FindAsync(s => s.ParentMeetingRoomId == meetingRoom.Id && s.EndedAt == null, ct: ct);

        var now = DateTime.UtcNow;
        foreach (var session in activeSessions)
        {
            session.EndedAt = now;
            _unitOfWork.BreakoutSessionRepository.Update(session);
        }

        if (activeSessions.Any())
            await _unitOfWork.SaveChangesAsync(ct);

        // Idempotent — relay unconditionally so a host double-click (or a retry after a
        // dropped response) is harmless for clients that already returned to the main room.
        await PublishRelayAsync("BreakoutsEnded", translationRoomId, new { });

        return Result.Success(true);
    }

    public async Task<Result<int>> ExpireDueBreakoutsAsync(
        DateTime utcNow,
        CancellationToken ct = default)
    {
        var candidates = await _unitOfWork.BreakoutSessionRepository
            .FindAsync(
                session => session.EndedAt == null &&
                           session.StartedAt.HasValue &&
                           session.DurationSeconds.HasValue,
                ct: ct);
        var dueSessions = candidates
            .Where(session =>
                session.StartedAt!.Value.AddSeconds(session.DurationSeconds!.Value) <= utcNow)
            .ToList();
        if (dueSessions.Count == 0)
            return Result.Success(0);

        foreach (var session in dueSessions)
        {
            session.EndedAt = utcNow;
            _unitOfWork.BreakoutSessionRepository.Update(session);
        }
        await _unitOfWork.SaveChangesAsync(ct);

        foreach (var parentMeetingRoomId in dueSessions
                     .Select(session => session.ParentMeetingRoomId)
                     .Distinct())
        {
            var meetingRoom = await _unitOfWork.MeetingRoomRepository.GetByIdAsync(
                parentMeetingRoomId,
                ct);
            if (meetingRoom is not null)
            {
                await PublishRelayAsync(
                    "BreakoutsEnded",
                    meetingRoom.TranslationRoomId,
                    new { reason = "duration_elapsed" });
            }
        }

        return Result.Success(dueSessions.Count);
    }

    public async Task<Result<BreakoutJoinInfoDto>> GetMyAssignmentAsync(Guid translationRoomId, Guid callerUserId, CancellationToken ct = default)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<BreakoutJoinInfoDto>("Meeting room not found.", ErrorCodes.NotFound);

        var myAssignments = await _unitOfWork.BreakoutAssignmentRepository
            .FindAsync(a => a.UserId == callerUserId, ct: ct);
        if (!myAssignments.Any())
            return Result.Failure<BreakoutJoinInfoDto>("No active breakout assignment.", ErrorCodes.NotFound);

        var mySessionIds = myAssignments.Select(a => a.BreakoutSessionId).ToHashSet();
        var activeSessions = await _unitOfWork.BreakoutSessionRepository
            .FindAsync(s => s.ParentMeetingRoomId == meetingRoom.Id && s.EndedAt == null && mySessionIds.Contains(s.Id), ct: ct);
        var session = activeSessions.FirstOrDefault();
        if (session == null)
            return Result.Failure<BreakoutJoinInfoDto>("No active breakout assignment.", ErrorCodes.NotFound);

        var participantIdentity = callerUserId.ToString();
        var participantName = await ResolveDisplayNameAsync(translationRoomId, participantIdentity);

        var tokenResult = _tokenService.GenerateToken(
            roomName: session.ProviderRoomName,
            participantIdentity: participantIdentity,
            participantName: participantName,
            canPublish: true,
            canSubscribe: true);

        if (!tokenResult.IsSuccess)
            return Result.Failure<BreakoutJoinInfoDto>(tokenResult.Error ?? "Failed to generate token", ErrorCodes.InternalServerError);

        return Result.Success(new BreakoutJoinInfoDto
        {
            SessionId = session.Id,
            Label = session.Label,
            ProviderRoomName = session.ProviderRoomName,
            Token = tokenResult.Value!,
            ParticipantIdentity = participantIdentity,
            DurationSeconds = session.DurationSeconds,
            StartedAt = session.StartedAt
        });
    }

    private async Task<string> ResolveDisplayNameAsync(Guid translationRoomId, string userIdString)
    {
        try
        {
            var grpcPartsResult = await _grpcService.GetParticipantsAsync(translationRoomId);
            if (grpcPartsResult.IsSuccess && grpcPartsResult.Value != null)
            {
                var p = grpcPartsResult.Value.Participants.FirstOrDefault(x => x.Id == userIdString);
                if (p != null && !string.IsNullOrEmpty(p.DisplayName))
                    return p.DisplayName;
            }
        }
        catch
        {
            // Best-effort, same as MeetingRoomService.JoinMeetingAsync's display-name lookup —
            // falls back below rather than failing the whole join.
        }

        return "Participant";
    }

    private static JsonElement ToRelayJson<T>(T value) => JsonSerializer.SerializeToElement(value, RelayJsonOptions);

    private Task<Result> PublishRelayAsync(string command, Guid translationRoomId, object extraFields)
    {
        var payload = new Dictionary<string, object?>
        {
            ["Command"] = command,
            ["RoomId"] = translationRoomId.ToString()
        };
        foreach (var property in extraFields.GetType().GetProperties())
            payload[property.Name] = property.GetValue(extraFields);

        return _redisService.PublishEventAsync(GatewayCommandsChannel, payload);
    }

    private async Task<bool> IsHostAsync(Guid translationRoomId, MeetingRoom meetingRoom, Guid callerUserId)
    {
        var roomCacheKey = $"meeting:room:{translationRoomId}";
        var roomDetailsResult = await _redisService.GetCacheAsync<Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        var roomDetails = roomDetailsResult.Value;

        if (roomDetails == null)
        {
            var grpcResult = await _grpcService.GetRoomDetailsAsync(translationRoomId);
            if (!grpcResult.IsSuccess || grpcResult.Value == null)
                return false;
            roomDetails = grpcResult.Value;
        }

        bool isOriginalHost = roomDetails.HostId == callerUserId.ToString();
        bool isActiveHost = meetingRoom.ActiveHostId == callerUserId;
        return isOriginalHost || isActiveHost;
    }
}
