using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Authorization;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Authorization;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <summary>
/// Sessions are the Start/Resume → Pause/End windows of a room. Every method here used to run for
/// any authenticated caller against any room id: <c>StartSessionAsync</c> only checked that the
/// room existed, and <c>UpdateSessionAsync</c>/<c>EndSessionAsync</c> checked only that the session
/// belonged to the room in the route — a condition the attacker controls both halves of. That made
/// it possible to mark another workspace's meeting ENDED, or to inject sessions that split its
/// transcript, from a plain user token.
///
/// The two predicates used here are the ones the rest of the service already uses; neither is a new
/// spelling. See <see cref="RoomReadAccess"/> (WT-304) and <see cref="RoomHostAccess"/>.
/// </summary>
public class TranslationRoomSessionService : ITranslationRoomSessionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomSessionRepository _translationRoomSessionRepository;
    private readonly IWorkspaceMemberDirectory _workspaceMemberDirectory;
    private readonly ILogger<TranslationRoomSessionService> _logger;

    public TranslationRoomSessionService(
        IUnitOfWork unitOfWork,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        ILogger<TranslationRoomSessionService> logger)
    {
        _unitOfWork = unitOfWork;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _translationRoomSessionRepository = _unitOfWork.TranslationRoomSessionRepository;
        _workspaceMemberDirectory = workspaceMemberDirectory;
        _logger = logger;
    }

    public async Task<Result<TranslationRoomSessionDto>> StartSessionAsync(Guid roomId, CreateTranslationRoomSessionDto dto, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null)
            {
                return Result.Failure<TranslationRoomSessionDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            }

            if (!await HasHostAuthorityAsync(room, requestedByUserId, ct))
            {
                return Result.Failure<TranslationRoomSessionDto>(
                    TranslationRoomSessionConstants.ErrorUnauthorizedManageSession,
                    ErrorCodes.Forbidden);
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

    public async Task<Result<List<TranslationRoomSessionDto>>> GetSessionsAsync(Guid roomId, Guid requestedByUserId, string? requestedByEmail = null, CancellationToken ct = default)
    {
        try
        {
            // Read, not write: gated on the same predicate the rooms list and the artifacts guard
            // use, so the three web callers that legitimately fetch this — the room detail page,
            // the transcript panel's session bucketing, and the AI summaries page — keep working
            // for exactly the rooms their user can already see, including a user who is in the
            // room only by a standing email invitation.
            if (!await CanReadRoomAsync(roomId, requestedByUserId, requestedByEmail, ct))
            {
                return Result.Failure<List<TranslationRoomSessionDto>>(
                    TranslationRoomSessionConstants.ErrorUnauthorizedViewSessions,
                    ErrorCodes.Forbidden);
            }

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

    public async Task<Result<TranslationRoomSessionDto>> UpdateSessionAsync(Guid roomId, Guid sessionId, UpdateTranslationRoomSessionDto dto, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null)
            {
                return Result.Failure<TranslationRoomSessionDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            }

            // Before the session lookup on purpose: an unauthorized caller must not be able to use
            // the difference between "session not found" and "session does not belong to this room"
            // as an oracle for session ids in rooms they cannot see.
            if (!await HasHostAuthorityAsync(room, requestedByUserId, ct))
            {
                return Result.Failure<TranslationRoomSessionDto>(
                    TranslationRoomSessionConstants.ErrorUnauthorizedManageSession,
                    ErrorCodes.Forbidden);
            }

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

    public async Task<Result<TranslationRoomSessionDto>> EndSessionAsync(Guid roomId, Guid sessionId, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null)
            {
                return Result.Failure<TranslationRoomSessionDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            }

            if (!await HasHostAuthorityAsync(room, requestedByUserId, ct))
            {
                return Result.Failure<TranslationRoomSessionDto>(
                    TranslationRoomSessionConstants.ErrorUnauthorizedManageSession,
                    ErrorCodes.Forbidden);
            }

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

    private Task<bool> HasHostAuthorityAsync(TranslationRoom room, Guid requestedByUserId, CancellationToken ct)
        => RoomHostAccess.HasHostAuthorityAsync(room, requestedByUserId, _workspaceMemberDirectory, ct);

    /// <summary>
    /// Same shape as <c>TranslationRoomService.CanAccessRoomAsync</c>, including its deliberate
    /// synchronousness: <c>AnyAsync</c> would need an EF async query provider that the unit tests'
    /// in-memory IQueryable does not implement.
    /// </summary>
    private Task<bool> CanReadRoomAsync(Guid roomId, Guid userId, string? userEmail, CancellationToken ct)
    {
        return Task.FromResult(_translationRoomRepository
            .Query()
            .Where(r => r.Id == roomId && r.DeletedAt == null && r.IsActive)
            .Any(RoomReadAccess.IsReadableBy(userId, userEmail)));
    }
}
