using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Authorization;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Authorization;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc />
public class MeetingMinutesService : IMeetingMinutesService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMemberDirectory _workspaceMemberDirectory;
    private readonly ILogger<MeetingMinutesService> _logger;

    public MeetingMinutesService(
        IUnitOfWork unitOfWork,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        ILogger<MeetingMinutesService> logger)
    {
        _unitOfWork = unitOfWork;
        _workspaceMemberDirectory = workspaceMemberDirectory;
        _logger = logger;
    }

    public async Task<Result<MeetingMinutesDto>> GetCurrentAsync(
        Guid roomId, Guid userId, string? userEmail, CancellationToken ct = default)
    {
        var readable = await _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(r => r.Id == roomId && r.DeletedAt == null && r.IsActive)
            .AnyAsync(RoomReadAccess.IsReadableBy(userId, userEmail), ct);

        if (!readable)
        {
            // NotFound rather than Forbidden: a caller who cannot read the room must not learn
            // from this endpoint that the room exists.
            return Result.Failure<MeetingMinutesDto>(
                MeetingMinutesConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
        }

        var minutes = await _unitOfWork.MeetingMinutesRepository.GetCurrentByRoomIdAsync(roomId, ct);
        if (minutes == null)
        {
            return Result.Failure<MeetingMinutesDto>(
                MeetingMinutesConstants.ErrorMinutesNotFound, ErrorCodes.NotFound);
        }

        return Result.Success(await ToDtoAsync(minutes, ct));
    }

    public async Task<Result<MeetingMinutesDto>> CreateDraftAsync(
        Guid roomId, Guid userId, CancellationToken ct = default)
    {
        var gate = await AuthorizeManageAsync(roomId, userId, ct);
        if (!gate.IsSuccess) return Result.Failure<MeetingMinutesDto>(gate.Error ?? MeetingMinutesConstants.ErrorMinutesNotFound, gate.ErrorCode);
        var room = gate.Value!;

        // A meeting still running has no closing time and an attendance list that is still moving.
        // Drawing minutes from it would produce a document that is wrong by the time it is read.
        if (!string.Equals(room.Status, "ENDED", StringComparison.Ordinal))
        {
            return Result.Failure<MeetingMinutesDto>(
                MeetingMinutesConstants.ErrorMeetingNotEnded, ErrorCodes.InvalidState);
        }

        var existing = await _unitOfWork.MeetingMinutesRepository.GetCurrentByRoomIdAsync(roomId, ct);
        if (existing != null)
        {
            // Idempotent on purpose. Pressing "lập biên bản" twice must not consume a second
            // minutes number, and must never overwrite edits somebody has already made.
            return Result.Success(await ToDtoAsync(existing, ct));
        }

        var participants = await _unitOfWork.TranslationRoomParticipantRepository
            .GetByRoomIdAsync(roomId, ct) ?? new List<TranslationRoomParticipant>();

        var summaryJson = await LoadSummaryContentAsync(roomId, ct);
        var now = DateTime.UtcNow;

        var minutes = new MeetingMinutes
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            WorkspaceId = room.WorkspaceId,
            MinutesNo = await NextMinutesNoAsync(room.WorkspaceId, now.Year, ct),
            Status = MeetingMinutesConstants.StatusDraft,
            Version = 1,
            IsCurrent = true,
            // Filled by the second-pass transcription work: until a re-transcription can happen
            // there is only one version of the transcript, so "which one was this drawn from" has
            // no meaningful answer to record.
            BasedOnTranscriptVersion = null,
            DraftedByEngine = MeetingMinutesDrafter.DraftEngine,
            DraftedAt = now,
            EditCountVsDraft = 0,
            Content = MeetingMinutesDrafter.BuildContent(room, participants, summaryJson),
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
            UpdatedBy = userId
        };

        await _unitOfWork.MeetingMinutesRepository.AddAsync(minutes, ct);

        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two secretaries pressed at once and both counted the same number. The unique index
            // rejected the loser, which is what it is for — a duplicated minutes number would be
            // far worse than asking them to press again.
            _logger.LogWarning("Minutes number collision drawing up minutes for room {RoomId}", roomId);
            return Result.Failure<MeetingMinutesDto>(
                MeetingMinutesConstants.ErrorNumberCollision, ErrorCodes.Conflict);
        }

        _logger.LogInformation(
            "Drew up minutes {MinutesNo} for room {RoomId}", minutes.MinutesNo, roomId);

        return Result.Success(await ToDtoAsync(minutes, ct));
    }

    public async Task<Result<MeetingMinutesDto>> UpdateContentAsync(
        Guid roomId, Guid minutesId, Guid userId, string contentJson, CancellationToken ct = default)
    {
        var loaded = await LoadForWriteAsync(roomId, minutesId, userId, ct);
        if (!loaded.IsSuccess) return Result.Failure<MeetingMinutesDto>(loaded.Error ?? MeetingMinutesConstants.ErrorMinutesNotFound, loaded.ErrorCode);
        var minutes = loaded.Value!;

        if (string.Equals(minutes.Status, MeetingMinutesConstants.StatusApproved, StringComparison.Ordinal))
        {
            return Result.Failure<MeetingMinutesDto>(
                MeetingMinutesConstants.ErrorApprovedIsImmutable, ErrorCodes.InvalidState);
        }

        minutes.Content = contentJson;
        minutes.UpdatedAt = DateTime.UtcNow;
        minutes.UpdatedBy = userId;

        _unitOfWork.MeetingMinutesRepository.Update(minutes);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(await ToDtoAsync(minutes, ct));
    }

    public async Task<Result<MeetingMinutesDto>> SignAsync(
        Guid roomId, Guid minutesId, Guid userId, CancellationToken ct = default)
    {
        var loaded = await LoadForWriteAsync(roomId, minutesId, userId, ct);
        if (!loaded.IsSuccess) return Result.Failure<MeetingMinutesDto>(loaded.Error ?? MeetingMinutesConstants.ErrorMinutesNotFound, loaded.ErrorCode);
        var minutes = loaded.Value!;

        if (string.Equals(minutes.Status, MeetingMinutesConstants.StatusApproved, StringComparison.Ordinal))
        {
            return Result.Failure<MeetingMinutesDto>(
                MeetingMinutesConstants.ErrorApprovedIsImmutable, ErrorCodes.InvalidState);
        }

        var now = DateTime.UtcNow;
        minutes.SecretaryParticipantId = await ResolveParticipantIdAsync(roomId, userId, ct);
        minutes.SecretarySignedAt = now;
        minutes.Status = MeetingMinutesConstants.StatusInReview;

        // Counted against the draft the machine produced, not against the previous save. This is
        // the number a reader uses to decide whether anybody actually read the document.
        minutes.EditCountVsDraft = MeetingMinutesDrafter.CountEdits(
            await RebuildDraftAsync(roomId, ct), minutes.Content);

        minutes.UpdatedAt = now;
        minutes.UpdatedBy = userId;

        _unitOfWork.MeetingMinutesRepository.Update(minutes);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Minutes {MinutesNo} signed by the secretary with {Edits} change(s) against the draft",
            minutes.MinutesNo, minutes.EditCountVsDraft);

        return Result.Success(await ToDtoAsync(minutes, ct));
    }

    public async Task<Result<MeetingMinutesDto>> ApproveAsync(
        Guid roomId, Guid minutesId, Guid userId, CancellationToken ct = default)
    {
        var loaded = await LoadForWriteAsync(roomId, minutesId, userId, ct);
        if (!loaded.IsSuccess) return Result.Failure<MeetingMinutesDto>(loaded.Error ?? MeetingMinutesConstants.ErrorMinutesNotFound, loaded.ErrorCode);
        var minutes = loaded.Value!;

        if (string.Equals(minutes.Status, MeetingMinutesConstants.StatusApproved, StringComparison.Ordinal))
        {
            return Result.Success(await ToDtoAsync(minutes, ct));
        }

        // Order matters and is the point of having two acts: the secretary is answerable for the
        // content, the chair for accepting it. Approving something nobody has signed would make
        // the secretary line decorative.
        if (minutes.SecretarySignedAt == null)
        {
            return Result.Failure<MeetingMinutesDto>(
                MeetingMinutesConstants.ErrorSignBeforeApprove, ErrorCodes.InvalidState);
        }

        var now = DateTime.UtcNow;
        minutes.ChairParticipantId = await ResolveParticipantIdAsync(roomId, userId, ct);
        minutes.ChairApprovedAt = now;
        minutes.Status = MeetingMinutesConstants.StatusApproved;
        minutes.UpdatedAt = now;
        minutes.UpdatedBy = userId;

        _unitOfWork.MeetingMinutesRepository.Update(minutes);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("Minutes {MinutesNo} approved for room {RoomId}", minutes.MinutesNo, roomId);

        return Result.Success(await ToDtoAsync(minutes, ct));
    }

    public async Task<Result<MeetingMinutesDto>> ReviseAsync(
        Guid roomId, Guid minutesId, Guid userId, CancellationToken ct = default)
    {
        var loaded = await LoadForWriteAsync(roomId, minutesId, userId, ct);
        if (!loaded.IsSuccess) return Result.Failure<MeetingMinutesDto>(loaded.Error ?? MeetingMinutesConstants.ErrorMinutesNotFound, loaded.ErrorCode);
        var approved = loaded.Value!;

        if (!string.Equals(approved.Status, MeetingMinutesConstants.StatusApproved, StringComparison.Ordinal))
        {
            // Nothing to revise: an unapproved document is still editable in place.
            return Result.Failure<MeetingMinutesDto>(
                MeetingMinutesConstants.ErrorNotApproved, ErrorCodes.InvalidState);
        }

        var now = DateTime.UtcNow;

        // The approved row keeps its status and its signatures and surrenders only the head
        // pointer. What was signed stays exactly as it was signed.
        approved.IsCurrent = false;
        approved.UpdatedAt = now;
        approved.UpdatedBy = userId;
        _unitOfWork.MeetingMinutesRepository.Update(approved);

        var revision = new MeetingMinutes
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            WorkspaceId = approved.WorkspaceId,
            // Same number, new version. A revision of BB-2026-0007 is still BB-2026-0007 —
            // renumbering it would break every reference anybody had already written down.
            MinutesNo = approved.MinutesNo,
            Status = MeetingMinutesConstants.StatusDraft,
            Version = approved.Version + 1,
            IsCurrent = true,
            PreviousMinutesId = approved.Id,
            BasedOnTranscriptVersion = approved.BasedOnTranscriptVersion,
            DraftedByEngine = approved.DraftedByEngine,
            DraftedAt = now,
            EditCountVsDraft = 0,
            Content = approved.Content,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
            UpdatedBy = userId
        };

        await _unitOfWork.MeetingMinutesRepository.AddAsync(revision, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Opened revision v{Version} of minutes {MinutesNo}", revision.Version, revision.MinutesNo);

        return Result.Success(await ToDtoAsync(revision, ct));
    }

    // ------------------------------------------------------------------ internals

    private async Task<Result<TranslationRoom>> AuthorizeManageAsync(
        Guid roomId, Guid userId, CancellationToken ct)
    {
        var room = await _unitOfWork.TranslationRoomRepository.GetByIdAsync(roomId, ct);
        if (room == null || room.DeletedAt != null)
        {
            return Result.Failure<TranslationRoom>(
                MeetingMinutesConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
        }

        if (!await RoomHostAccess.HasHostAuthorityAsync(room, userId, _workspaceMemberDirectory, ct))
        {
            return Result.Failure<TranslationRoom>(
                MeetingMinutesConstants.ErrorUnauthorizedManage, ErrorCodes.Forbidden);
        }

        return Result.Success(room);
    }

    private async Task<Result<MeetingMinutes>> LoadForWriteAsync(
        Guid roomId, Guid minutesId, Guid userId, CancellationToken ct)
    {
        var gate = await AuthorizeManageAsync(roomId, userId, ct);
        if (!gate.IsSuccess) return Result.Failure<MeetingMinutes>(gate.Error ?? MeetingMinutesConstants.ErrorMinutesNotFound, gate.ErrorCode);

        var minutes = await _unitOfWork.MeetingMinutesRepository.GetByIdAsync(minutesId, ct);

        // The room check is not redundant with the id lookup: without it, a host of room A could
        // act on minutes belonging to room B by quoting its id.
        if (minutes == null || minutes.TranslationRoomId != roomId)
        {
            return Result.Failure<MeetingMinutes>(
                MeetingMinutesConstants.ErrorMinutesNotFound, ErrorCodes.NotFound);
        }

        return Result.Success(minutes);
    }

    /// <summary>The latest SUMMARY_EXPORT's stored JSON, or null when the meeting has none.</summary>
    private async Task<string?> LoadSummaryContentAsync(Guid roomId, CancellationToken ct)
    {
        var artifacts = await _unitOfWork.TranslationRoomArtifactRepository
            .GetArtifactsByRoomIdAsync(roomId, ct);

        return artifacts?
            .Where(a => a.ArtifactType == ArtifactType.SUMMARY_EXPORT.ToString())
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault()?
            .Content;
    }

    /// <summary>
    /// The draft as it would be drawn up right now, for comparison against what the secretary is
    /// signing. Rebuilt rather than stored: keeping a frozen copy of the draft beside the live
    /// document doubles the row and gives two things that can disagree.
    /// </summary>
    private async Task<string?> RebuildDraftAsync(Guid roomId, CancellationToken ct)
    {
        var room = await _unitOfWork.TranslationRoomRepository.GetByIdAsync(roomId, ct);
        if (room == null) return null;

        var participants = await _unitOfWork.TranslationRoomParticipantRepository
            .GetByRoomIdAsync(roomId, ct) ?? new List<TranslationRoomParticipant>();

        return MeetingMinutesDrafter.BuildContent(
            room, participants, await LoadSummaryContentAsync(roomId, ct));
    }

    private async Task<Guid?> ResolveParticipantIdAsync(Guid roomId, Guid userId, CancellationToken ct)
    {
        var participants = await _unitOfWork.TranslationRoomParticipantRepository
            .GetByRoomIdAsync(roomId, ct);

        // Null when the host never joined their own meeting. The signature is still recorded
        // through UpdatedBy and the name below; inventing a participant row to fill this column
        // would put somebody in the attendance list who was not there.
        return participants?.FirstOrDefault(p => p.UserId == userId)?.Id;
    }

    private async Task<string> NextMinutesNoAsync(Guid workspaceId, int year, CancellationToken ct)
    {
        var used = await _unitOfWork.MeetingMinutesRepository
            .CountForWorkspaceYearAsync(workspaceId, year, ct);
        return $"BB-{year}-{used + 1:D4}";
    }

    private async Task<MeetingMinutesDto> ToDtoAsync(MeetingMinutes minutes, CancellationToken ct)
    {
        var participants = await _unitOfWork.TranslationRoomParticipantRepository
            .GetByRoomIdAsync(minutes.TranslationRoomId, ct);

        string? NameOf(Guid? participantId) => participantId == null
            ? null
            : participants?.FirstOrDefault(p => p.Id == participantId)?.DisplayName;

        return new MeetingMinutesDto(
            minutes.Id,
            minutes.TranslationRoomId,
            minutes.MinutesNo,
            minutes.Status,
            minutes.Version,
            minutes.IsCurrent,
            minutes.PreviousMinutesId,
            minutes.BasedOnTranscriptVersion,
            minutes.DraftedByEngine,
            minutes.DraftedAt,
            minutes.SecretaryParticipantId,
            NameOf(minutes.SecretaryParticipantId),
            minutes.SecretarySignedAt,
            minutes.ChairParticipantId,
            NameOf(minutes.ChairParticipantId),
            minutes.ChairApprovedAt,
            minutes.EditCountVsDraft,
            minutes.Content,
            minutes.CreatedAt,
            minutes.UpdatedAt);
    }
}
