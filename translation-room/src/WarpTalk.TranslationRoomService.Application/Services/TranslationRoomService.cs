using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomService : ITranslationRoomService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _participantRepository;

    public TranslationRoomService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _participantRepository = _unitOfWork.TranslationRoomParticipantRepository;
    }

    public async Task<Result<TranslationRoomDto>> CreateTranslationRoomAsync(CreateTranslationRoomRequest request, Guid hostId, CancellationToken ct = default)
    {
        // 1. Determine initial status
        var status = request.ScheduledAt.HasValue ? RoomStatus.Scheduled : RoomStatus.Waiting;

        // 2. Generate unique 12-char alphanumeric TranslationRoomCode
        string roomCode;
        bool exists;
        do
        {
            roomCode = RoomCodeGenerator.GenerateCode();
            exists = await _translationRoomRepository.ExistsByCodeAsync(roomCode, ct);
        } while (exists);

        // 3. Create entity
        var room = TranslationRoomMapper.ToEntity(request, hostId, roomCode, status);

        // 4. Save via repository and UnitOfWork
        await _translationRoomRepository.AddAsync(room, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // 5. Return mapped response
        return Result.Success(TranslationRoomMapper.ToResponseDto(room));
    }

    public async Task<Result<TranslationRoomDto>> GetTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default)
    {
        var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        
        if (translationRoom == null)
            return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        return Result.Success(TranslationRoomMapper.ToResponseDto(translationRoom));
    }

    public async Task<Result<TranslationRoomParticipantDto>> JoinTranslationRoomAsync(Guid translationRoomId, Guid userId, JoinTranslationRoomRequest request, CancellationToken ct = default)
    {
        var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        if (translationRoom == null || translationRoom.Status == RoomStatus.Ended)
            return Result.Failure<TranslationRoomParticipantDto>(TranslationRoomConstants.ErrorRoomNotActive, ErrorCodes.TranslationRoomNotActive);

        var participant = TranslationRoomMapper.ToParticipantEntity(translationRoomId, userId, request);

        await _participantRepository.AddAsync(participant, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(TranslationRoomMapper.ToParticipantDto(participant));
    }

    public async Task<Result> EndTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        
        if (translationRoom == null)
            return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        if (translationRoom.HostId != hostId)
            return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedEndRoom, ErrorCodes.Unauthorized);

        translationRoom.Status = RoomStatus.Ended;
        translationRoom.EndedAt = DateTime.UtcNow;
        translationRoom.UpdatedAt = DateTime.UtcNow;

        _translationRoomRepository.Update(translationRoom);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}
