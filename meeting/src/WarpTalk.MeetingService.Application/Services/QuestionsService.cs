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

public class QuestionsService : IQuestionsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomGrpcService _grpcService;
    private readonly IRedisService _redisService;

    // See PollsService — same established Redis→Gateway relay mechanism for privileged
    // (host-only) actions, since the Gateway hub cannot do a real host check on its own.
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";

    private static readonly JsonSerializerOptions RelayJsonOptions = new(JsonSerializerOptions.Default)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public QuestionsService(IUnitOfWork unitOfWork, ITranslationRoomGrpcService grpcService, IRedisService redisService)
    {
        _unitOfWork = unitOfWork;
        _grpcService = grpcService;
        _redisService = redisService;
    }

    public async Task<Result<QuestionDto>> AskAsync(Guid translationRoomId, Guid callerUserId, CreateQuestionRequest request, CancellationToken ct = default)
    {
        var body = request.Body?.Trim();
        if (string.IsNullOrWhiteSpace(body))
            return Result.Failure<QuestionDto>("Question body is required.", ErrorCodes.ValidationError);

        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<QuestionDto>("Meeting room not found.", ErrorCodes.NotFound);

        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == callerUserId, ct: ct);
        bool isActiveParticipant = participant != null && participant.IsActive && participant.LeftAt == null;
        if (meetingRoom.CreatedBy != callerUserId && !isActiveParticipant)
            return Result.Failure<QuestionDto>("Not an active participant.", ErrorCodes.Forbidden);

        var displayName = !string.IsNullOrWhiteSpace(request.DisplayName)
            ? request.DisplayName!.Trim()
            : (participant?.ProviderIdentity ?? "Unknown User");

        var question = new Question
        {
            Id = Guid.NewGuid(),
            MeetingRoomId = meetingRoom.Id,
            AskedBy = callerUserId,
            AskedByDisplayName = displayName,
            Body = body!,
            Status = "open",
            CreatedAt = DateTime.UtcNow
        };
        await _unitOfWork.Repository<Question>().AddAsync(question, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var dto = BuildDto(question, upvoteCount: 0, upvotedByMe: false);
        await PublishRelayAsync("QuestionAsked", translationRoomId, new { Question = ToRelayJson(dto) });

        return Result.Success(dto);
    }

    public async Task<Result<QuestionDto>> UpvoteAsync(Guid translationRoomId, Guid questionId, Guid callerUserId, CancellationToken ct = default)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<QuestionDto>("Meeting room not found.", ErrorCodes.NotFound);

        if (!await IsActiveParticipantAsync(meetingRoom, callerUserId, ct))
            return Result.Failure<QuestionDto>("Not an active participant.", ErrorCodes.Forbidden);

        var question = await _unitOfWork.Repository<Question>().FirstOrDefaultAsync(q => q.Id == questionId && q.MeetingRoomId == meetingRoom.Id, ct: ct);
        if (question == null)
            return Result.Failure<QuestionDto>("Question not found.", ErrorCodes.NotFound);

        var voteRepo = _unitOfWork.Repository<QuestionVote>();
        var existingVote = await voteRepo.FirstOrDefaultAsync(v => v.QuestionId == questionId && v.UserId == callerUserId, ct: ct);

        bool upvotedByMe;
        if (existingVote != null)
        {
            voteRepo.Remove(existingVote);
            upvotedByMe = false;
        }
        else
        {
            await voteRepo.AddAsync(new QuestionVote { Id = Guid.NewGuid(), QuestionId = questionId, UserId = callerUserId }, ct);
            upvotedByMe = true;
        }
        await _unitOfWork.SaveChangesAsync(ct);

        var upvoteCount = (await voteRepo.FindAsync(v => v.QuestionId == questionId, ct: ct)).Count;
        var dto = BuildDto(question, upvoteCount, upvotedByMe);

        await PublishRelayAsync("QuestionUpvoted", translationRoomId, new { QuestionId = questionId.ToString(), UpvoteCount = upvoteCount });

        return Result.Success(dto);
    }

    public async Task<Result<QuestionDto>> AnswerAsync(Guid translationRoomId, Guid questionId, Guid callerUserId, CancellationToken ct = default)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<QuestionDto>("Meeting room not found.", ErrorCodes.NotFound);

        var question = await _unitOfWork.Repository<Question>().FirstOrDefaultAsync(q => q.Id == questionId && q.MeetingRoomId == meetingRoom.Id, ct: ct);
        if (question == null)
            return Result.Failure<QuestionDto>("Question not found.", ErrorCodes.NotFound);

        if (!await IsHostAsync(translationRoomId, meetingRoom, callerUserId))
            return Result.Failure<QuestionDto>("Only the host can mark a question as answered.", ErrorCodes.Forbidden);

        var upvoteCount = (await _unitOfWork.Repository<QuestionVote>().FindAsync(v => v.QuestionId == questionId, ct: ct)).Count;
        var upvotedByMe = await _unitOfWork.Repository<QuestionVote>().AnyAsync(v => v.QuestionId == questionId && v.UserId == callerUserId, ct);

        if (question.Status == "open")
        {
            question.Status = "answered";
            question.AnsweredAt = DateTime.UtcNow;
            _unitOfWork.Repository<Question>().Update(question);
            await _unitOfWork.SaveChangesAsync(ct);

            await PublishRelayAsync("QuestionAnswered", translationRoomId, new { QuestionId = questionId.ToString() });
        }

        return Result.Success(BuildDto(question, upvoteCount, upvotedByMe));
    }

    public async Task<Result<List<QuestionDto>>> ListAsync(Guid translationRoomId, Guid callerUserId, CancellationToken ct = default)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId, ct: ct);
        if (meetingRoom == null)
            return Result.Failure<List<QuestionDto>>("Meeting room not found.", ErrorCodes.NotFound);

        var questions = (await _unitOfWork.Repository<Question>().FindAsync(q => q.MeetingRoomId == meetingRoom.Id, ct: ct)).ToList();
        var questionIds = questions.Select(q => q.Id).ToHashSet();
        var allVotes = (await _unitOfWork.Repository<QuestionVote>().FindAsync(v => questionIds.Contains(v.QuestionId), ct: ct)).ToList();

        var dtos = questions
            .Select(q => BuildDto(
                q,
                upvoteCount: allVotes.Count(v => v.QuestionId == q.Id),
                upvotedByMe: allVotes.Any(v => v.QuestionId == q.Id && v.UserId == callerUserId)))
            .OrderByDescending(d => d.UpvoteCount)
            .ThenBy(d => d.CreatedAt)
            .ToList();

        return Result.Success(dtos);
    }

    private static QuestionDto BuildDto(Question question, int upvoteCount, bool upvotedByMe)
    {
        return new QuestionDto
        {
            Id = question.Id,
            AskedBy = question.AskedBy,
            AskedByDisplayName = question.AskedByDisplayName,
            Body = question.Body,
            Status = question.Status,
            UpvoteCount = upvoteCount,
            UpvotedByMe = upvotedByMe,
            CreatedAt = question.CreatedAt,
            AnsweredAt = question.AnsweredAt
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
