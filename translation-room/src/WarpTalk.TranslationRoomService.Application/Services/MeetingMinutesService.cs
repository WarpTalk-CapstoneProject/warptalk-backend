using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Authorization;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Authorization;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc />
public class MeetingMinutesService : IMeetingMinutesService
{
    /// <summary>Registered in NotificationValidator in the same commit as this producer.</summary>
    private const string ActionItemAssignedNotificationType = "ACTION_ITEM_ASSIGNED";

    /// <summary>
    /// The library page's ceiling. Every row carries its whole Content document, so an
    /// unclamped pageSize is a caller-controlled way to ask for the workspace's entire minutes
    /// archive in one response.
    /// </summary>
    private const int MaxLibraryPageSize = 200;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMemberDirectory _workspaceMemberDirectory;
    private readonly IMeetingMinutesDocumentWriter _documentWriter;
    /// <summary>Nullable, matching TranslationRoomService: a deployment without it still runs.</summary>
    private readonly WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? _notificationClient;
    private readonly string _frontendBaseUrl;
    private readonly ILogger<MeetingMinutesService> _logger;

    public MeetingMinutesService(
        IUnitOfWork unitOfWork,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        IMeetingMinutesDocumentWriter documentWriter,
        ILogger<MeetingMinutesService> logger,
        WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? notificationClient = null,
        IOptions<AppSettings>? appSettings = null)
    {
        _unitOfWork = unitOfWork;
        _workspaceMemberDirectory = workspaceMemberDirectory;
        _documentWriter = documentWriter;
        _notificationClient = notificationClient;
        _frontendBaseUrl = appSettings?.Value.FrontendBaseUrl?.TrimEnd('/') ?? "http://localhost:3000";
        _logger = logger;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

        // A DRAFT is not published, and this was the loosest read in the whole record.
        //
        // The gate above is RoomReadAccess, which admits the host, every participant AND anyone
        // merely invited by email who never attended. Applied to a DRAFT that means a machine
        // wrote a biên bản, nobody checked it, and it was already readable by more people than the
        // transcript it was drawn from — while the transcript, the summary and the recording all
        // sat behind the host's Publish switch.
        //
        // The document's own lifecycle is the publish act: DRAFT -> IN_REVIEW is a person signing
        // their name to it, and that is the point at which it becomes somebody's word rather than
        // a model's output. So the draft stays with the people who can act on it — the host, a
        // workspace Owner/Admin — and everyone else sees it the moment it is signed.
        if (string.Equals(minutes.Status, MeetingMinutesConstants.StatusDraft, StringComparison.OrdinalIgnoreCase))
        {
            var room = await _unitOfWork.TranslationRoomRepository.GetByIdAsync(roomId, ct);
            var canManage = room != null
                && await RoomHostAccess.HasHostAuthorityAsync(room, userId, _workspaceMemberDirectory, ct);

            if (!canManage)
            {
                return Result.Failure<MeetingMinutesDto>(
                    MeetingMinutesConstants.ErrorMinutesNotPublished, ErrorCodes.Forbidden);
            }
        }

        return Result.Success(await ToDtoAsync(minutes, ct));
    }

    /// <inheritdoc />
    public async Task<Result<WorkspaceMinutesResponse>> ListForWorkspaceAsync(
        Guid workspaceId,
        GetWorkspaceMinutesRequest request,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
        {
            return Result.Failure<WorkspaceMinutesResponse>(
                "A workspace is required to list minutes.", ErrorCodes.ValidationError);
        }

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = Math.Clamp(request.PageSize, 1, MaxLibraryPageSize);

        // The same predicate GetCurrentAsync applies to ONE room, expressed here as a set. Written
        // as a subquery rather than as a materialised id list on purpose: a workspace's readable
        // rooms is unbounded, and pulling every id into memory to send back as an IN clause is the
        // shape that works in a demo and falls over in a tenant.
        var readableRoomIds = _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(r => r.WorkspaceId == workspaceId && r.DeletedAt == null && r.IsActive)
            .Where(RoomReadAccess.IsReadableBy(userId, userEmail))
            .Select(r => r.Id);

        var query = _unitOfWork.MeetingMinutesRepository
            .Query()
            .Where(m => m.WorkspaceId == workspaceId
                && m.IsCurrent
                && readableRoomIds.Contains(m.TranslationRoomId));

        // #344 closed this on GetCurrentAsync and left the door beside it open.
        //
        // That change made an unsigned draft readable only by the people who can act on it, on the
        // grounds that a machine wrote it and nobody has checked a word. This list is the same
        // documents one door wider — room-read across a whole workspace, no status filter — and
        // every row carries its entire Content (see GetWorkspaceMinutesRequest, which sets a small
        // page size for exactly that reason). So somebody holding unaccepted email invitations to a
        // few rooms could page through the drafts of all of them.
        //
        // The per-room gate asks RoomHostAccess, which cannot come along: workspace Owner/Admin is
        // a gRPC answer and EF has no translation for it. What SQL can answer is the room's own
        // host columns, so an Owner/Admin sees somebody else's draft on the room's Minutes tab but
        // not in the library listing. That asymmetry is the cost of keeping the wide door narrow,
        // and it errs in the direction the narrow door already errs in.
        var hostedRoomIds = _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(r => r.WorkspaceId == workspaceId
                && (r.HostId == userId || r.ActiveHostId == userId))
            .Select(r => r.Id);

        query = query.Where(m => m.Status != MeetingMinutesConstants.StatusDraft
            || hostedRoomIds.Contains(m.TranslationRoomId));

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var status = request.Status.Trim().ToUpperInvariant();
            query = query.Where(m => m.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            // Case-insensitive substring over the document's IDENTITY — its number and the meeting
            // it belongs to. Deliberately the same three fields the room history searches, so one
            // term narrows every kind of record in the library the same way.
            //
            // NOT the document body, for two reasons. `content` is a jsonb column, so `lower()`
            // does not apply to it at all (Postgres answers "function lower(jsonb) does not
            // exist"), and the room history does not search artifact bodies either — searching
            // inside minutes while transcripts and summaries beside them matched on title only
            // would make one kind of record behave unlike the rest of the page for no reason a
            // reader could see.
            //
            // Body search happens in the web, folded for diacritics, over the page it has already
            // loaded — every row here carries its whole Content, so there is nothing to fetch. The
            // real answer for the whole archive is one full-text index covering all three kinds,
            // which is a change to make once rather than three times.
            var search = request.Search.Trim().ToLowerInvariant();
            query = query.Where(m =>
                m.MinutesNo.ToLower().Contains(search)
                || m.TranslationRoom.Title.ToLower().Contains(search)
                || m.TranslationRoom.TranslationRoomCode.ToLower().Contains(search));
        }

        var total = await query.CountAsync(ct);

        // Ordered by the MEETING, not by the document. A minutes row's own CreatedAt is when
        // somebody pressed "draw up the draft", which can be weeks after the meeting and in a
        // different order — so ordering by it interleaves last month's meetings among this
        // week's, and a reader scanning for "the one from Tuesday" cannot find it.
        var rows = await query
            .OrderByDescending(m => m.TranslationRoom.EndedAt ?? m.CreatedAt)
            .ThenByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new
            {
                Minutes = m,
                RoomTitle = m.TranslationRoom.Title,
                RoomCode = m.TranslationRoom.TranslationRoomCode,
                RoomHostId = m.TranslationRoom.HostId,
                RoomStatus = m.TranslationRoom.Status,
                RoomEndedAt = m.TranslationRoom.EndedAt,
            })
            .ToListAsync(ct);

        // One roster query for the whole page. ToDtoAsync asks per document, which is correct for
        // a single read and is a query per row here — the N+1 that turns a 50-row library page
        // into 51 round trips.
        var roomIds = rows.Select(row => row.Minutes.TranslationRoomId).Distinct().ToList();
        var participantNames = await _unitOfWork.TranslationRoomParticipantRepository
            .Query()
            .Where(p => roomIds.Contains(p.TranslationRoomId))
            .Select(p => new { p.Id, p.DisplayName })
            .ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);

        string? NameOf(Guid? participantId) =>
            participantId != null && participantNames.TryGetValue(participantId.Value, out var name)
                ? name
                : null;

        var items = rows
            .Select(row => new WorkspaceMinutesItemDto(
                MapToDto(row.Minutes, NameOf),
                row.RoomTitle,
                row.RoomCode,
                row.RoomHostId,
                row.RoomStatus,
                row.RoomEndedAt))
            .ToList();

        return Result.Success(new WorkspaceMinutesResponse(items, total, page, pageSize));
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

        // A recurring meeting inherits whatever the previous occurrences left open. One-off
        // meetings have no series and therefore nothing to inherit, which is the common case.
        var carriedOver = room.SeriesId.HasValue
            ? await _unitOfWork.MeetingActionItemRepository
                .GetOpenForSeriesAsync(room.SeriesId.Value, roomId, ct)
            : new List<MeetingActionItem>();

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
            Content = MeetingMinutesDrafter.BuildContent(room, participants, summaryJson, carriedOver),
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

        // Approval is the moment the record becomes the record, so it is the moment a commitment
        // becomes a task. Doing this from a draft would put work in people's lists that the
        // meeting never ratified, and take it back out whenever the secretary edited a line.
        var created = await MaterialiseActionItemsAsync(minutes, ct);

        await _unitOfWork.SaveChangesAsync(ct);

        // After the commit, never before: telling somebody about a task whose row failed to save
        // is worse than telling them a moment late.
        await NotifyAssigneesAsync(roomId, created, ct);

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

    public async Task<Result<MinutesExportFile>> ExportDocxAsync(
        Guid roomId, Guid userId, string? userEmail, CancellationToken ct = default)
    {
        // Deliberately the same gate as reading the minutes on screen, not the write gate.
        // Downloading is reading; a separate, stricter rule here would mean the people who were
        // at the meeting could see the record but not keep a copy of it.
        var current = await GetCurrentAsync(roomId, userId, userEmail, ct);
        if (!current.IsSuccess)
        {
            return Result.Failure<MinutesExportFile>(
                current.Error ?? MeetingMinutesConstants.ErrorMinutesNotFound, current.ErrorCode);
        }

        var minutes = current.Value!;
        var content = TryReadContent(minutes.Content);
        if (content == null)
        {
            // The columns alone would render a file with a number, a signature block and nothing
            // between them — a document that looks complete and says nothing.
            return Result.Failure<MinutesExportFile>(
                MeetingMinutesConstants.ErrorContentUnreadable, ErrorCodes.InvalidState);
        }

        var bytes = _documentWriter.WriteDocx(minutes, content);

        // The minutes number is the file name, because that is what the recipient will file it
        // under. Version only appears once there is more than one, so an ordinary document does
        // not arrive looking like a revision.
        var suffix = minutes.Version > 1 ? $"-v{minutes.Version}" : string.Empty;
        return Result.Success(new MinutesExportFile(
            bytes,
            $"{minutes.MinutesNo}{suffix}.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// Turn the approved minutes' action items into rows somebody can be assigned and can close.
    ///
    /// IDEMPOTENT
    ///     Approving twice — a retry, a double click — must not double somebody's task list, so a
    ///     minutes version that has already produced its rows produces nothing.
    ///
    /// A REVISION INHERITS PROGRESS, MATCHED ON THE CITATION
    ///     Version N+1 of a minutes describes the same meeting, so a task somebody has already
    ///     ticked off must not come back OPEN because the secretary fixed a typo elsewhere. The
    ///     match is on `atMs` — the same join key the bilingual layout uses, and for the same
    ///     reason: position is not evidence that two lines are the same commitment.
    /// </summary>
    private async Task<List<MeetingActionItem>> MaterialiseActionItemsAsync(
        MeetingMinutes minutes, CancellationToken ct)
    {
        var created = new List<MeetingActionItem>();
        if (await _unitOfWork.MeetingActionItemRepository.AnyForMinutesAsync(minutes.Id, ct)) return created;

        var content = TryReadContent(minutes.Content);
        // "actionItems" only. The carried-over section quotes tasks that already exist as rows
        // in an earlier meeting; materialising them again would show one commitment twice in
        // somebody's list and split its history across two rows.
        var items = content?.Sections
            .FirstOrDefault(section => section.Key == "actionItems")?
            .Items;

        if (items == null || items.Count == 0) return created;

        var room = await _unitOfWork.TranslationRoomRepository.GetByIdAsync(minutes.TranslationRoomId, ct);
        var participants = await _unitOfWork.TranslationRoomParticipantRepository
            .GetByRoomIdAsync(minutes.TranslationRoomId, ct) ?? new List<TranslationRoomParticipant>();

        var existing = await _unitOfWork.MeetingActionItemRepository
            .GetByRoomIdAsync(minutes.TranslationRoomId, ct);

        var now = DateTime.UtcNow;

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Text)) continue;

            var owner = ActionItemOwnerResolver.Resolve(item.Owner, participants);

            // Only an unambiguous citation identifies the same commitment across versions. With
            // no citation the safest reading is "a new line", which starts OPEN — the alternative
            // is inheriting the status of whatever happened to look similar.
            var prior = item.AtMs.HasValue
                ? existing.SingleOrDefault(candidate => candidate.AtMs == item.AtMs)
                : null;

            var row = new MeetingActionItem
            {
                Id = Guid.CreateVersion7(),
                TranslationRoomId = minutes.TranslationRoomId,
                WorkspaceId = minutes.WorkspaceId,
                SourceMinutesId = minutes.Id,
                SeriesId = room?.SeriesId,
                Task = item.Text.Trim(),
                // Kept exactly as the meeting said it, whether or not it resolved to anybody.
                OwnerName = string.IsNullOrWhiteSpace(item.Owner) ? null : item.Owner!.Trim(),
                OwnerParticipantId = owner?.Id,
                AssigneeUserId = owner?.UserId,
                AtMs = item.AtMs,
                Status = prior?.Status ?? MeetingActionItemConstants.StatusOpen,
                ClosedAt = prior?.ClosedAt,
                ClosedBy = prior?.ClosedBy,
                DueDate = prior?.DueDate,
                CreatedAt = now,
                UpdatedAt = now
            };

            await _unitOfWork.MeetingActionItemRepository.AddAsync(row, ct);
            created.Add(row);
        }

        return created;
    }

    /// <summary>
    /// Tell each person the meeting gave work to.
    ///
    /// Only rows that RESOLVED to a user: an unmatched owner has nobody to notify, and notifying
    /// the host instead would turn "somebody said Nhi" into "you have a task".
    ///
    /// Failures are logged and swallowed. The minutes are approved and the tasks are committed by
    /// the time this runs; losing a notification must not turn a successful approval into an error
    /// the host has to retry.
    /// </summary>
    private async Task NotifyAssigneesAsync(
        Guid roomId, IReadOnlyCollection<MeetingActionItem> created, CancellationToken ct)
    {
        if (created.Count == 0) return;

        if (_notificationClient is null)
        {
            _logger.LogInformation(
                "action_item_notification_skipped: reason=client_unavailable RoomId={RoomId}", roomId);
            return;
        }

        var room = await _unitOfWork.TranslationRoomRepository.GetByIdAsync(roomId, ct);
        var title = room?.Title ?? "Meeting";
        var link = $"{_frontendBaseUrl}/room/{roomId}";

        foreach (var item in created.Where(item => item.AssigneeUserId.HasValue))
        {
            try
            {
                var request = new WarpTalk.Shared.Protos.SendNotificationRequest
                {
                    UserId = item.AssigneeUserId!.Value.ToString(),
                    Type = ActionItemAssignedNotificationType,
                    Title = $"You were given a task in \"{title}\"",
                    Body = item.Task,
                    ActionUrl = link
                };
                // Exactly the four fields NotificationValidator declares for this type. An
                // undeclared field would reject the whole payload, not be ignored.
                request.Metadata.Add("room_id", roomId.ToString());
                request.Metadata.Add("room_title", title);
                request.Metadata.Add("action_item_id", item.Id.ToString());
                request.Metadata.Add("task", item.Task);

                await _notificationClient.SendNotificationAsync(request, cancellationToken: ct);

                // The success side too, so "it fired and something downstream dropped it" can be
                // told apart from "it never fired" — the distinction that took four notification
                // types months to make.
                _logger.LogInformation(
                    "action_item_notification_sent: RoomId={RoomId} UserId={UserId} ItemId={ItemId}",
                    roomId, item.AssigneeUserId, item.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to notify {UserId} about action item {ItemId}. The task itself is unaffected.",
                    item.AssigneeUserId, item.Id);
            }
        }
    }

    private static MeetingMinutesContent? TryReadContent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<MeetingMinutesContent>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

        return MapToDto(minutes, participantId => participantId == null
            ? null
            : participants?.FirstOrDefault(p => p.Id == participantId)?.DisplayName);
    }

    /// <summary>
    /// The row as the web reads it, given a way to name a participant.
    ///
    /// The name lookup is a parameter rather than a query because the two callers resolve it
    /// differently and must not each restate the mapping: the single-document read asks for one
    /// room's roster, and the library read batches every room on the page into one query. Sharing
    /// the projection is what keeps a minutes document from describing itself one way in the
    /// library and another way when opened.
    /// </summary>
    private static MeetingMinutesDto MapToDto(MeetingMinutes minutes, Func<Guid?, string?> nameOf)
        => new(
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
            nameOf(minutes.SecretaryParticipantId),
            minutes.SecretarySignedAt,
            minutes.ChairParticipantId,
            nameOf(minutes.ChairParticipantId),
            minutes.ChairApprovedAt,
            minutes.EditCountVsDraft,
            minutes.Content,
            minutes.CreatedAt,
            minutes.UpdatedAt);
}
