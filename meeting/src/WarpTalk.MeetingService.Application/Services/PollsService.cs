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

public class PollsService : IPollsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomGrpcService _grpcService;
    private readonly IRedisService _redisService;

    // Same Redis Pub/Sub channel MeetingRoomService.PublishGatewayCommandAsync publishes to
    // and the Gateway's TranslationRoomRedisSubscriberService relays into TranslationRoomHub —
    // reused here rather than adding a new hub-direct method, since privileged actions (create/
    // close poll) need a real host check that only this REST-layer service can do.
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";

    private static readonly JsonSerializerOptions RelayJsonOptions = new(JsonSerializerOptions.Default)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PollsService(IUnitOfWork unitOfWork, ITranslationRoomGrpcService grpcService, IRedisService redisService)
    {
        _unitOfWork = unitOfWork;
        _grpcService = grpcService;
        _redisService = redisService;
    }

    public async Task<Result<PollDto>> CreatePollAsync(Guid translationRoomId, Guid callerUserId, CreatePollRequest request, CancellationToken ct = default)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrWhiteSpace(question))
            return Result.Failure<PollDto>("Question is required.", ErrorCodes.ValidationError);

        var options = (request.Options ?? new List<string>())
            .Select(o => o?.Trim())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o!)
            .ToList();

        if (options.Count < 2 || options.Count > 6)
            return Result.Failure<PollDto>("A poll needs between 2 and 6 options.", ErrorCodes.ValidationError);

        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<PollDto>("Meeting room not found.", ErrorCodes.NotFound);

        if (!await IsHostAsync(translationRoomId, meetingRoom, callerUserId))
            return Result.Failure<PollDto>("Only the host can create a poll.", ErrorCodes.Forbidden);

        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            MeetingRoomId = meetingRoom.Id,
            CreatedBy = callerUserId,
            Question = question!,
            IsMultipleChoice = request.IsMultipleChoice,
            Status = "open",
            CreatedAt = DateTime.UtcNow
        };
        await _unitOfWork.PollRepository.AddAsync(poll, ct);

        var pollOptions = options.Select((label, index) => new PollOption
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Label = label,
            Position = index
        }).ToList();
        foreach (var option in pollOptions)
            await _unitOfWork.PollOptionRepository.AddAsync(option, ct);

        await _unitOfWork.SaveChangesAsync(ct);

        var dto = BuildDto(poll, pollOptions, votes: new List<PollVote>(), callerUserId);
        await PublishRelayAsync("PollCreated", translationRoomId, new { Poll = ToRelayJson(dto) });

        return Result.Success(dto);
    }

    public async Task<Result<PollDto>> VoteAsync(Guid translationRoomId, Guid pollId, Guid callerUserId, VotePollRequest request, CancellationToken ct = default)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<PollDto>("Meeting room not found.", ErrorCodes.NotFound);

        if (!await IsActiveParticipantAsync(meetingRoom, callerUserId, ct))
            return Result.Failure<PollDto>("Not an active participant.", ErrorCodes.Forbidden);

        var poll = await _unitOfWork.PollRepository.FirstOrDefaultAsync(p => p.Id == pollId && p.MeetingRoomId == meetingRoom.Id, ct: ct);
        if (poll == null)
            return Result.Failure<PollDto>("Poll not found.", ErrorCodes.NotFound);

        if (poll.Status != "open")
            return Result.Failure<PollDto>("This poll is closed.", ErrorCodes.InvalidState);

        var optionIds = (request.OptionIds ?? new List<Guid>()).Distinct().ToList();
        if (optionIds.Count == 0)
            return Result.Failure<PollDto>("Select at least one option.", ErrorCodes.ValidationError);

        if (!poll.IsMultipleChoice && optionIds.Count > 1)
            return Result.Failure<PollDto>("This poll only allows a single choice.", ErrorCodes.ValidationError);

        var pollOptions = (await _unitOfWork.PollOptionRepository.FindAsync(o => o.PollId == pollId, ct: ct)).ToList();
        var validOptionIds = pollOptions.Select(o => o.Id).ToHashSet();
        if (!optionIds.All(validOptionIds.Contains))
            return Result.Failure<PollDto>("One or more options do not belong to this poll.", ErrorCodes.ValidationError);

        // Re-vote replaces any prior vote(s) from this caller for this poll.
        var voteRepo = _unitOfWork.PollVoteRepository;
        var priorVotes = await voteRepo.FindAsync(v => v.PollId == pollId && v.UserId == callerUserId, ct: ct);
        foreach (var vote in priorVotes)
            voteRepo.Remove(vote);

        foreach (var optionId in optionIds)
        {
            await voteRepo.AddAsync(new PollVote
            {
                Id = Guid.NewGuid(),
                PollId = pollId,
                OptionId = optionId,
                UserId = callerUserId,
                CreatedAt = DateTime.UtcNow
            }, ct);
        }

        await _unitOfWork.SaveChangesAsync(ct);

        var allVotes = (await voteRepo.FindAsync(v => v.PollId == pollId, ct: ct)).ToList();
        var dto = BuildDto(poll, pollOptions, allVotes, callerUserId);

        var tally = pollOptions.ToDictionary(o => o.Id.ToString(), o => allVotes.Count(v => v.OptionId == o.Id));
        await PublishRelayAsync("PollVoted", translationRoomId, new { PollId = pollId.ToString(), Tally = tally });

        return Result.Success(dto);
    }

    public async Task<Result<PollDto>> CloseAsync(Guid translationRoomId, Guid pollId, Guid callerUserId, CancellationToken ct = default)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<PollDto>("Meeting room not found.", ErrorCodes.NotFound);

        var poll = await _unitOfWork.PollRepository.FirstOrDefaultAsync(p => p.Id == pollId && p.MeetingRoomId == meetingRoom.Id, ct: ct);
        if (poll == null)
            return Result.Failure<PollDto>("Poll not found.", ErrorCodes.NotFound);

        if (!await IsHostAsync(translationRoomId, meetingRoom, callerUserId))
            return Result.Failure<PollDto>("Only the host can close a poll.", ErrorCodes.Forbidden);

        var pollOptions = (await _unitOfWork.PollOptionRepository.FindAsync(o => o.PollId == pollId, ct: ct)).ToList();
        var allVotes = (await _unitOfWork.PollVoteRepository.FindAsync(v => v.PollId == pollId, ct: ct)).ToList();

        if (poll.Status == "open")
        {
            poll.Status = "closed";
            poll.ClosedAt = DateTime.UtcNow;
            _unitOfWork.PollRepository.Update(poll);
            await _unitOfWork.SaveChangesAsync(ct);

            var closedDto = BuildDto(poll, pollOptions, allVotes, callerUserId);
            await PublishRelayAsync("PollClosed", translationRoomId, new { PollId = pollId.ToString(), FinalResult = ToRelayJson(closedDto) });
            return Result.Success(closedDto);
        }

        return Result.Success(BuildDto(poll, pollOptions, allVotes, callerUserId));
    }

    public async Task<Result<List<PollDto>>> ListAsync(Guid translationRoomId, Guid callerUserId, CancellationToken ct = default)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<List<PollDto>>("Meeting room not found.", ErrorCodes.NotFound);

        var polls = (await _unitOfWork.PollRepository.FindAsync(p => p.MeetingRoomId == meetingRoom.Id, ct: ct))
            .OrderBy(p => p.CreatedAt)
            .ToList();
        var pollIds = polls.Select(p => p.Id).ToHashSet();

        var allOptions = (await _unitOfWork.PollOptionRepository.FindAsync(o => pollIds.Contains(o.PollId), ct: ct)).ToList();
        var allVotes = (await _unitOfWork.PollVoteRepository.FindAsync(v => pollIds.Contains(v.PollId), ct: ct)).ToList();

        var dtos = polls.Select(p => BuildDto(
            p,
            allOptions.Where(o => o.PollId == p.Id).ToList(),
            allVotes.Where(v => v.PollId == p.Id).ToList(),
            callerUserId)).ToList();

        return Result.Success(dtos);
    }

    private static PollDto BuildDto(Poll poll, List<PollOption> options, List<PollVote> votes, Guid callerUserId)
    {
        return new PollDto
        {
            Id = poll.Id,
            CreatedBy = poll.CreatedBy,
            Question = poll.Question,
            IsMultipleChoice = poll.IsMultipleChoice,
            Status = poll.Status,
            CreatedAt = poll.CreatedAt,
            ClosedAt = poll.ClosedAt,
            Options = options
                .OrderBy(o => o.Position)
                .Select(o => new PollOptionDto
                {
                    Id = o.Id,
                    Label = o.Label,
                    Position = o.Position,
                    VoteCount = votes.Count(v => v.OptionId == o.Id)
                })
                .ToList(),
            MyVotedOptionIds = votes.Where(v => v.UserId == callerUserId).Select(v => v.OptionId).ToList()
        };
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

    private async Task<bool> IsActiveParticipantAsync(MeetingRoom meetingRoom, Guid callerUserId, CancellationToken ct)
    {
        if (meetingRoom.CreatedBy == callerUserId)
            return true;

        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == callerUserId, ct: ct);
        return participant != null && participant.IsActive && participant.LeftAt == null;
    }

    private async Task<bool> IsHostAsync(Guid translationRoomId, MeetingRoom meetingRoom, Guid callerUserId)
    {
        var roomCacheKey = $"meeting:room:v2:{translationRoomId}";
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
