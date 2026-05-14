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
        var status = request.ScheduledAt.HasValue ? RoomStatus.SCHEDULED : RoomStatus.WAITING;

        // 2. Generate unique 12-char alphanumeric TranslationRoomCode
        string roomCode;
        bool exists;
        do
        {
            roomCode = RoomCodeGenerator.GenerateCode();
            exists = await _translationRoomRepository.ExistsByCodeAsync(roomCode, TranslationRoomConstants.TerminalStatuses, ct);
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

    public async Task<Result<JoinTranslationRoomResponse>> JoinTranslationRoomAsync(JoinTranslationRoomRequest request, Guid userId, CancellationToken ct = default)
    {
        var translationRoom = await _translationRoomRepository.GetByCodeAsync(request.TranslationRoomCode, TranslationRoomConstants.TerminalStatuses, ct);
        if (translationRoom == null)
            return Result.Failure<JoinTranslationRoomResponse>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        // BR-006: Upsert participant record
        var participant = await _participantRepository.GetByRoomAndUserAsync(translationRoom.Id, userId, ct);

        if (participant == null)
        {
            participant = TranslationRoomMapper.ToParticipantEntity(translationRoom.Id, userId, request);
            
            // BR-004: Host check
            if (translationRoom.HostId == userId)
            {
                participant.Role = TranslationRoomParticipantRole.HOST;
            }
            
            await _participantRepository.AddAsync(participant, ct);
        }
        else
        {
            // Update existing participant context
            participant.DisplayName = request.DisplayName;
            participant.ListenLanguage = request.ListenLanguage;
            participant.SpeakLanguage = request.SpeakLanguage;
            participant.Status = TranslationRoomParticipantStatus.CONNECTED;
            participant.UpdatedAt = DateTime.UtcNow;
            _participantRepository.Update(participant);
        }

        await _unitOfWork.SaveChangesAsync(ct);

        // BR-008: Return comprehensive context
        return Result.Success(new JoinTranslationRoomResponse(
            TranslationRoomMapper.ToResponseDto(translationRoom),
            TranslationRoomMapper.ToParticipantDto(participant)
        ));
    }

    public async Task<Result> EndTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
        
        if (translationRoom == null)
            return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        if (translationRoom.HostId != hostId)
            return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedEndRoom, ErrorCodes.Unauthorized);

        translationRoom.Status = RoomStatus.ENDED;
        translationRoom.EndedAt = DateTime.UtcNow;
        translationRoom.UpdatedAt = DateTime.UtcNow;

        _translationRoomRepository.Update(translationRoom);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}
