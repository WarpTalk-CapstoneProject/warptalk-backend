using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Services;

public class MeetingService : IMeetingService
{
    private readonly IUnitOfWork _unitOfWork;

    public MeetingService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<MeetingDto>> CreateMeetingAsync(CreateMeetingRequest request, Guid hostId, CancellationToken ct = default)
    {
        var targetLangsJson = string.IsNullOrWhiteSpace(request.TargetLanguages) 
            ? "[]" 
            : (request.TargetLanguages.Trim().StartsWith("[") 
                ? request.TargetLanguages 
                : System.Text.Json.JsonSerializer.Serialize(request.TargetLanguages.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())));

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId ?? Guid.Empty,
            HostId = hostId,
            Title = request.Title,
            Description = request.Description,
            MeetingCode = GenerateMeetingCode(),
            Status = "scheduled",
            MeetingType = request.MeetingType,
            MaxParticipants = request.MaxParticipants,
            SourceLanguage = request.SourceLanguage,
            TargetLanguages = targetLangsJson,
            Settings = "{}",
            ScheduledAt = request.ScheduledAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var repo = _unitOfWork.Repository<Meeting>();
        await repo.AddAsync(meeting);
        await _unitOfWork.SaveChangesAsync(); // Assumes IUnitOfWork SaveChangesAsync doesn't strictly require CT, or we pass nothing if not implemented

        return Result.Success(MapToDto(meeting));
    }

    public async Task<Result<MeetingDto>> GetMeetingAsync(Guid meetingId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<Meeting>();
        var meeting = await repo.GetByIdAsync(meetingId);
        
        if (meeting == null)
            return Result.Failure<MeetingDto>("Meeting not found", ErrorCodes.NotFound);

        return Result.Success(MapToDto(meeting));
    }

    public async Task<Result<MeetingParticipantDto>> JoinMeetingAsync(Guid meetingId, Guid userId, JoinMeetingRequest request, CancellationToken ct = default)
    {
        var meetingRepo = _unitOfWork.Repository<Meeting>();
        var meeting = await meetingRepo.GetByIdAsync(meetingId);
        if (meeting == null || meeting.Status == "ended")
            return Result.Failure<MeetingParticipantDto>("Meeting not active or found", ErrorCodes.NotFound);

        var participant = new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            MeetingId = meetingId,
            UserId = userId,
            DisplayName = request.DisplayName,
            Role = "participant",
            ListenLanguage = request.ListenLanguage,
            SpeakLanguage = request.SpeakLanguage,
            Status = "joined",
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var participantRepo = _unitOfWork.Repository<MeetingParticipant>();
        await participantRepo.AddAsync(participant);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(new MeetingParticipantDto(
            participant.Id,
            participant.MeetingId,
            participant.UserId,
            participant.DisplayName,
            participant.Role,
            participant.ListenLanguage,
            participant.SpeakLanguage,
            participant.Status,
            participant.JoinedAt
        ));
    }

    public async Task<Result> EndMeetingAsync(Guid meetingId, Guid hostId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<Meeting>();
        var meeting = await repo.GetByIdAsync(meetingId);
        
        if (meeting == null)
            return Result.Failure("Meeting not found", ErrorCodes.NotFound);

        if (meeting.HostId != hostId)
            return Result.Failure("Unauthorized. Only host can end meeting.", ErrorCodes.Unauthorized);

        meeting.Status = "ended";
        meeting.EndedAt = DateTime.UtcNow;
        meeting.UpdatedAt = DateTime.UtcNow;

        repo.Update(meeting);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    private string GenerateMeetingCode()
    {
        var chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = new Random();
        var code = new string(Enumerable.Repeat(chars, 9)
            .Select(s => s[random.Next(s.Length)]).ToArray());
        return $"{code.Substring(0, 3)}-{code.Substring(3, 3)}-{code.Substring(6, 3)}";
    }

    private MeetingDto MapToDto(Meeting meeting)
    {
        return new MeetingDto(
            meeting.Id,
            meeting.WorkspaceId,
            meeting.HostId,
            meeting.Title,
            meeting.Description,
            meeting.MeetingCode,
            meeting.Status,
            meeting.MeetingType,
            meeting.MaxParticipants,
            meeting.ScheduledAt,
            meeting.StartedAt,
            meeting.EndedAt,
            meeting.CreatedAt
        );
    }
}
