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
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomParticipantService : ITranslationRoomParticipantService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _participantRepository;
    private readonly IWorkspaceMemberDirectory _workspaceMemberDirectory;
    private readonly ILogger<TranslationRoomParticipantService> _logger;

    public TranslationRoomParticipantService(
        IUnitOfWork unitOfWork,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        ILogger<TranslationRoomParticipantService> logger)
    {
        _unitOfWork = unitOfWork;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _participantRepository = _unitOfWork.TranslationRoomParticipantRepository;
        _workspaceMemberDirectory = workspaceMemberDirectory;
        _logger = logger;
    }

    public async Task<Result<List<TranslationRoomParticipantDto>>> GetParticipantsAsync(Guid translationRoomId, GetParticipantsRequest request, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
                return Result.Failure<List<TranslationRoomParticipantDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            var requester = await _participantRepository.GetByRoomAndUserAsync(translationRoomId, requestedByUserId, ct);

            // WT-65 loosened this from "must be CONNECTED" to "any participant, any status" —
            // but the whole check was accidentally dropped instead of just the status condition,
            // leaving this endpoint callable by ANY authenticated user for ANY room. Restored with
            // the WT-65 intent kept: host or any participant (regardless of Status) may list
            // participants. ErrorCodes.Forbidden matches this method's own pinned unit test
            // (GetParticipantsAsync_ShouldReturnForbidden_WhenRequesterIsNotInRoom).
            if (room.HostId != requestedByUserId && requester == null)
            {
                return Result.Failure<List<TranslationRoomParticipantDto>>(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Forbidden);
            }

            var participants = await _participantRepository.FindAsync(p => p.TranslationRoomId == translationRoomId, "", ct);
            var query = participants.AsEnumerable();

            if (!string.IsNullOrEmpty(request.Search))
            {
                var search = request.Search.ToLower();
                query = query.Where(p => p.DisplayName.ToLower().Contains(search) || (p.UserId != null && p.UserId.Value.ToString().ToLower().Contains(search)));
            }

            if (!string.IsNullOrEmpty(request.Status))
            {
                query = query.Where(p => p.Status.ToString().Equals(request.Status, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(request.Role))
            {
                query = query.Where(p => p.Role.Equals(request.Role, StringComparison.OrdinalIgnoreCase));
            }

            query = request.SortBy?.ToLower() switch
            {
                "displayname" => request.IsDescending ? query.OrderByDescending(p => p.DisplayName) : query.OrderBy(p => p.DisplayName),
                "status" => request.IsDescending ? query.OrderByDescending(p => p.Status) : query.OrderBy(p => p.Status),
                "role" => request.IsDescending ? query.OrderByDescending(p => p.Role) : query.OrderBy(p => p.Role),
                _ => request.IsDescending ? query.OrderByDescending(p => p.JoinedAt) : query.OrderBy(p => p.JoinedAt)
            };

            var dtos = query.Select(p => p.ToDto()).ToList();

            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while getting participants for RoomId: {RoomId}", translationRoomId);
            return Result.Failure<List<TranslationRoomParticipantDto>>("An unexpected error occurred while getting participants.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UpdateParticipantAudioAsync(Guid translationRoomId, Guid participantId, UpdateParticipantAudioRequest request, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (room.HostId != requestedByUserId)
                return Result.Failure(TranslationRoomConstants.ErrorOnlyHostCanManageAudio, ErrorCodes.Forbidden);

            var participant = await _participantRepository.GetByIdAsync(participantId, ct);
            if (participant == null || participant.TranslationRoomId != translationRoomId)
                return Result.Failure(TranslationRoomConstants.ErrorParticipantNotFound, ErrorCodes.NotFound);

            // Per BR-1.3-005: "Disable translation audio" means stopping translated audio relay to the participant, not muting their mic.
            participant.IsTranslationAudioEnabled = request.IsTranslationAudioEnabled;
            participant.UpdatedAt = DateTime.UtcNow;

            _participantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating participant audio. RoomId: {RoomId}, ParticipantId: {ParticipantId}", translationRoomId, participantId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedUpdateParticipantAudio, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> AdmitParticipantAsync(Guid translationRoomId, Guid participantId, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // WT-188: admission is not host-only. The web client already grants host-like lobby
            // controls to workspace Owners/Admins (see room page's `isHost`), so restricting this to
            // room.HostId left them staring at an Approve button that always 403'd — while a plain
            // Member who happened to create the room could admit the Owner. Widened to "room host OR
            // workspace Owner/Admin", which is what the UI has always advertised.
            if (room.HostId != requestedByUserId
                && !await _workspaceMemberDirectory.IsOwnerOrAdminAsync(room.WorkspaceId, requestedByUserId, ct))
            {
                return Result.Failure(
                    "Only the host or a workspace owner/admin can admit participants.",
                    ErrorCodes.Forbidden);
            }

            var participant = await _participantRepository.GetByIdAsync(participantId, ct);
            if (participant == null || participant.TranslationRoomId != translationRoomId)
                return Result.Failure("Participant not found.", ErrorCodes.NotFound);

            if (participant.Status != TranslationRoomParticipantStatuses.Waiting)
                return Result.Failure("Participant is not in the waiting room.", ErrorCodes.ValidationError);

            participant.Status = TranslationRoomParticipantStatuses.Connected;
            participant.UpdatedAt = DateTime.UtcNow;

            _participantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while admitting participant. RoomId: {RoomId}, ParticipantId: {ParticipantId}", translationRoomId, participantId);
            return Result.Failure("An unexpected error occurred while admitting participant.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> KickParticipantAsync(Guid translationRoomId, Guid participantId, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (room.HostId != requestedByUserId)
                return Result.Failure(TranslationRoomConstants.ErrorOnlyHostCanKick, ErrorCodes.Forbidden);

            var participant = await _participantRepository.GetByIdAsync(participantId, ct);
            if (participant == null || participant.TranslationRoomId != translationRoomId)
                return Result.Failure(TranslationRoomConstants.ErrorParticipantNotFound, ErrorCodes.NotFound);

            if (participant.UserId == room.HostId)
                return Result.Failure(TranslationRoomConstants.ErrorCannotKickHost, ErrorCodes.ValidationError);

            participant.Status = TranslationRoomParticipantStatuses.Kicked;
            participant.UpdatedAt = DateTime.UtcNow;

            _participantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while kicking participant. RoomId: {RoomId}, ParticipantId: {ParticipantId}", translationRoomId, participantId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedKickParticipant, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> LeaveRoomAsync(Guid translationRoomId, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var participant = await _participantRepository.GetByRoomAndUserAsync(translationRoomId, requestedByUserId, ct);
            if (participant == null)
                return Result.Failure(TranslationRoomConstants.ErrorParticipantNotFound, ErrorCodes.NotFound);

            var leftAt = DateTime.UtcNow;
            participant.Status = TranslationRoomParticipantStatuses.Left;
            participant.LeftAt = leftAt;
            participant.UpdatedAt = leftAt;

            _participantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while leaving room. RoomId: {RoomId}, UserId: {UserId}", translationRoomId, requestedByUserId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedLeaveRoom, ErrorCodes.InternalServerError);
        }
    }
}
