using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
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
    private readonly ILogger<TranslationRoomService> _logger;

    public TranslationRoomService(IUnitOfWork unitOfWork, ILogger<TranslationRoomService> logger)
    {
        _unitOfWork = unitOfWork;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _participantRepository = _unitOfWork.TranslationRoomParticipantRepository;
        _logger = logger;
    }

    public async Task<Result<TranslationRoomDto>> CreateTranslationRoomAsync(CreateTranslationRoomRequest request, Guid hostId, CancellationToken ct = default)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating translation room for HostId: {HostId}", hostId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while creating the room.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomDto>> GetTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            
            if (translationRoom == null)
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            return Result.Success(TranslationRoomMapper.ToResponseDto(translationRoom));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching translation room: {RoomId}", translationRoomId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while fetching the room.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<JoinTranslationRoomResponse>> JoinTranslationRoomAsync(JoinTranslationRoomRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByCodeAsync(request.TranslationRoomCode, TranslationRoomConstants.TerminalStatuses, ct);
            if (translationRoom == null)
                return Result.Failure<JoinTranslationRoomResponse>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // BR-006: Upsert participant record
            var participant = await _participantRepository.GetByRoomAndUserAsync(translationRoom.Id, userId, ct);

            // BR-010: Block KICKED participants
            if (participant != null && participant.Status == TranslationRoomParticipantStatus.KICKED.ToString())
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

            if (participant == null)
            {
                var initialStatus = requiresApproval ? TranslationRoomParticipantStatus.WAITING : TranslationRoomParticipantStatus.CONNECTED;
                participant = TranslationRoomMapper.ToParticipantEntity(translationRoom.Id, userId, request, initialStatus);
                
                // BR-004: Host check
                if (translationRoom.HostId == userId)
                {
                    participant.Role = TranslationRoomParticipantRole.HOST.ToString();
                    participant.Status = TranslationRoomParticipantStatus.CONNECTED.ToString();
                }
                
                await _participantRepository.AddAsync(participant, ct);
            }
            else
            {
                // Update existing participant context
                participant.DisplayName = request.DisplayName;
                participant.ListenLanguage = request.ListenLanguage;
                participant.SpeakLanguage = request.SpeakLanguage;
                
                if (translationRoom.HostId == userId)
                {
                    participant.Status = TranslationRoomParticipantStatus.CONNECTED.ToString();
                }
                else
                {
                    // BR-009, BR-012: REJECTED, DISCONNECTED, or LEFT re-enters based on requires_approval
                    participant.Status = (requiresApproval ? TranslationRoomParticipantStatus.WAITING : TranslationRoomParticipantStatus.CONNECTED).ToString();
                }

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while joining translation room. UserId: {UserId}, RoomCode: {RoomCode}", userId, request.TranslationRoomCode);
            return Result.Failure<JoinTranslationRoomResponse>("An unexpected error occurred while joining the room.", ErrorCodes.InternalServerError);
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

            translationRoom.Status = RoomStatus.ENDED.ToString();
            translationRoom.EndedAt = DateTime.UtcNow;
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while ending translation room. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure("An unexpected error occurred while ending the room.", ErrorCodes.InternalServerError);
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

            if (translationRoom.Status != RoomStatus.SCHEDULED.ToString() && translationRoom.Status != RoomStatus.WAITING.ToString())
                return Result.Failure(TranslationRoomConstants.ErrorSettingsLocked, ErrorCodes.InvalidState);

            var newSettings = new TranslationRoomSettings 
            { 
                RequiresApproval = request.Settings.RequiresApproval 
            };

            translationRoom.Settings = System.Text.Json.JsonSerializer.Serialize(newSettings);
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating translation room settings. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure("An unexpected error occurred while updating the room settings.", ErrorCodes.InternalServerError);
        }
    }
}
