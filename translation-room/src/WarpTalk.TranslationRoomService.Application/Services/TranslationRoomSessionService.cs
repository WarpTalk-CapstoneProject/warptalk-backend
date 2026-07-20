using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomSessionService : ITranslationRoomSessionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomSessionRepository _translationRoomSessionRepository;
    private readonly ILogger<TranslationRoomSessionService> _logger;

    public TranslationRoomSessionService(
        IUnitOfWork unitOfWork,
        ILogger<TranslationRoomSessionService> logger)
    {
        _unitOfWork = unitOfWork;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _translationRoomSessionRepository = _unitOfWork.TranslationRoomSessionRepository;
        _logger = logger;
    }

    public async Task<Result<TranslationRoomSessionDto>> StartSessionAsync(Guid roomId, CreateTranslationRoomSessionDto dto, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null)
            {
                return Result.Failure<TranslationRoomSessionDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            }

            var session = TranslationRoomSessionMapper.ToEntity(roomId, dto);
            await _translationRoomSessionRepository.AddAsync(session, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(TranslationRoomSessionMapper.ToDto(session));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while starting a session for Room {RoomId}", roomId);
            return Result.Failure<TranslationRoomSessionDto>(TranslationRoomSessionConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<TranslationRoomSessionDto>>> GetSessionsAsync(Guid roomId, CancellationToken ct = default)
    {
        try
        {
            var sessions = await _translationRoomSessionRepository.GetSessionsByRoomIdAsync(roomId, ct);
            var dtos = sessions.Select(TranslationRoomSessionMapper.ToDto).ToList();
            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching sessions for Room {RoomId}", roomId);
            return Result.Failure<List<TranslationRoomSessionDto>>(TranslationRoomSessionConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomSessionDto>> UpdateSessionAsync(Guid roomId, Guid sessionId, UpdateTranslationRoomSessionDto dto, CancellationToken ct = default)
    {
        try
        {
            var session = await _translationRoomSessionRepository.GetByIdAsync(sessionId, ct);
            if (session == null)
            {
                return Result.Failure<TranslationRoomSessionDto>(TranslationRoomSessionConstants.ErrorSessionNotFound, ErrorCodes.NotFound);
            }

            if (session.TranslationRoomId != roomId)
            {
                return Result.Failure<TranslationRoomSessionDto>(TranslationRoomSessionConstants.ErrorSessionNotBelongToRoom, ErrorCodes.ValidationError);
            }

            bool updated = false;
            if (dto.Status != null && session.Status != dto.Status)
            {
                session.Status = dto.Status;
                updated = true;
            }

            if (dto.AudioUrl != null && session.AudioUrl != dto.AudioUrl)
            {
                session.AudioUrl = dto.AudioUrl;
                updated = true;
            }

            if (updated)
            {
                session.UpdatedAt = DateTime.UtcNow;
                _translationRoomSessionRepository.Update(session);
                await _unitOfWork.SaveChangesAsync(ct);
            }

            return Result.Success(TranslationRoomSessionMapper.ToDto(session));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating session {SessionId}", sessionId);
            return Result.Failure<TranslationRoomSessionDto>(TranslationRoomSessionConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomSessionDto>> EndSessionAsync(Guid roomId, Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            var session = await _translationRoomSessionRepository.GetByIdAsync(sessionId, ct);
            if (session == null)
            {
                return Result.Failure<TranslationRoomSessionDto>(TranslationRoomSessionConstants.ErrorSessionNotFound, ErrorCodes.NotFound);
            }

            if (session.TranslationRoomId != roomId)
            {
                return Result.Failure<TranslationRoomSessionDto>(TranslationRoomSessionConstants.ErrorSessionNotBelongToRoom, ErrorCodes.ValidationError);
            }

            session.Status = Domain.Enums.TranslationRoomSessionStatus.ENDED.ToString();
            session.EndedAt = DateTime.UtcNow;
            session.UpdatedAt = DateTime.UtcNow;
            _translationRoomSessionRepository.Update(session);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(TranslationRoomSessionMapper.ToDto(session));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while ending session {SessionId}", sessionId);
            return Result.Failure<TranslationRoomSessionDto>(TranslationRoomSessionConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }
}
