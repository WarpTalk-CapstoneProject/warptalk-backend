using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomService : ITranslationRoomService
{
    private readonly IUnitOfWork _unitOfWork;

    public TranslationRoomService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<TranslationRoomDto>> CreateTranslationRoomAsync(CreateTranslationRoomRequest request, Guid hostId, CancellationToken ct = default)
    {
        var normalizedType = NormalizeTranslationRoomType(request.TranslationRoomType);
        if (normalizedType is null)
            return Result.Failure<TranslationRoomDto>(
                "Invalid TranslationRoomType. Allowed values: instant, scheduled, one_to_one, group, webinar, b2b_virtual_mic.",
                ErrorCodes.ValidationError);

        var targetLangsJson = string.IsNullOrWhiteSpace(request.TargetLanguages) 
            ? "[]" 
            : (request.TargetLanguages.Trim().StartsWith("[") 
                ? request.TargetLanguages 
                : System.Text.Json.JsonSerializer.Serialize(request.TargetLanguages.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())));

        var translationRoom = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId ?? Guid.Empty,
            HostId = hostId,
            Title = request.Title,
            Description = request.Description,
            TranslationRoomCode = GenerateTranslationRoomCode(),
            Status = "scheduled",
            TranslationRoomType = normalizedType,
            MaxParticipants = request.MaxParticipants,
            SourceLanguage = request.SourceLanguage,
            TargetLanguages = targetLangsJson,
            Settings = "{}",
            ScheduledAt = request.ScheduledAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var repo = _unitOfWork.Repository<TranslationRoom>();
        await repo.AddAsync(translationRoom);
        await _unitOfWork.SaveChangesAsync(); // Assumes IUnitOfWork SaveChangesAsync doesn't strictly require CT, or we pass nothing if not implemented

        return Result.Success(MapToDto(translationRoom));
    }

    public async Task<Result<TranslationRoomDto>> GetTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<TranslationRoom>();
        var translationRoom = await repo.GetByIdAsync(translationRoomId);
        
        if (translationRoom == null)
            return Result.Failure<TranslationRoomDto>("TranslationRoom not found", ErrorCodes.NotFound);

        return Result.Success(MapToDto(translationRoom));
    }

    public async Task<Result<TranslationRoomParticipantDto>> JoinTranslationRoomAsync(Guid translationRoomId, Guid userId, JoinTranslationRoomRequest request, CancellationToken ct = default)
    {
        var translationRoomRepo = _unitOfWork.Repository<TranslationRoom>();
        var translationRoom = await translationRoomRepo.GetByIdAsync(translationRoomId);
        if (translationRoom == null || translationRoom.Status == "ended")
            return Result.Failure<TranslationRoomParticipantDto>("TranslationRoom not active or found", ErrorCodes.NotFound);

        var participant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            UserId = userId,
            DisplayName = request.DisplayName,
            Role = "participant",
            ListenLanguage = request.ListenLanguage,
            SpeakLanguage = request.SpeakLanguage,
            Status = "connected",
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var participantRepo = _unitOfWork.Repository<TranslationRoomParticipant>();
        await participantRepo.AddAsync(participant);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(new TranslationRoomParticipantDto(
            participant.Id,
            participant.TranslationRoomId,
            participant.UserId,
            participant.DisplayName,
            participant.Role,
            participant.ListenLanguage,
            participant.SpeakLanguage,
            participant.Status,
            participant.JoinedAt
        ));
    }

    public async Task<Result> EndTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<TranslationRoom>();
        var translationRoom = await repo.GetByIdAsync(translationRoomId);
        
        if (translationRoom == null)
            return Result.Failure("TranslationRoom not found", ErrorCodes.NotFound);

        if (translationRoom.HostId != hostId)
            return Result.Failure("Unauthorized. Only host can end translationRoom.", ErrorCodes.Unauthorized);

        translationRoom.Status = "ended";
        translationRoom.EndedAt = DateTime.UtcNow;
        translationRoom.UpdatedAt = DateTime.UtcNow;

        repo.Update(translationRoom);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    private string GenerateTranslationRoomCode()
    {
        var chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = new Random();
        var code = new string(Enumerable.Repeat(chars, 9)
            .Select(s => s[random.Next(s.Length)]).ToArray());
        return $"{code.Substring(0, 3)}-{code.Substring(3, 3)}-{code.Substring(6, 3)}";
    }

    private static string? NormalizeTranslationRoomType(string? requestedType)
    {
        if (string.IsNullOrWhiteSpace(requestedType))
            return null;

        var normalized = requestedType.Trim().ToLowerInvariant();
        return normalized switch
        {
            // Compatibility aliases used by current client payloads
            "instant" => "group",
            "scheduled" => "group",
            // Supported values in current DB constraint
            "one_to_one" => "one_to_one",
            "group" => "group",
            "webinar" => "webinar",
            "b2b_virtual_mic" => "b2b_virtual_mic",
            _ => null
        };
    }

    private TranslationRoomDto MapToDto(TranslationRoom translationRoom)
    {
        return new TranslationRoomDto(
            translationRoom.Id,
            translationRoom.WorkspaceId,
            translationRoom.HostId,
            translationRoom.Title,
            translationRoom.Description,
            translationRoom.TranslationRoomCode,
            translationRoom.Status,
            translationRoom.TranslationRoomType,
            translationRoom.MaxParticipants,
            translationRoom.ScheduledAt,
            translationRoom.StartedAt,
            translationRoom.EndedAt,
            translationRoom.CreatedAt
        );
    }
}
