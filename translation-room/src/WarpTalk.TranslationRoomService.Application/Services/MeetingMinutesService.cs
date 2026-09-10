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
using WarpTalk.TranslationRoomService.Application.Mappers;
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
    /// <summary>
    /// Null in a deployment with no office engine to convert with. PDF is a convenience on top of
    /// a document that already downloads, so its absence must not stop the service starting.
    /// </summary>
    private readonly IDocumentPdfConverter? _pdfConverter;
    /// <summary>Nullable, matching TranslationRoomService: a deployment without it still runs.</summary>
    private readonly WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? _notificationClient;
    /// <summary>
    /// Where a language the document does not carry comes from. Optional for the same reason the
    /// notification client is: every other thing this service does works without it, and a
    /// deployment that has not wired it should lose one read rather than fail to start.
    /// </summary>
    private readonly ITranslationRoomArtifactService? _summaryVariants;
    private readonly string _frontendBaseUrl;
    private readonly ILogger<MeetingMinutesService> _logger;

    public MeetingMinutesService(
        IUnitOfWork unitOfWork,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        IMeetingMinutesDocumentWriter documentWriter,
        ILogger<MeetingMinutesService> logger,
        WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? notificationClient = null,
        IOptions<AppSettings>? appSettings = null,
        IDocumentPdfConverter? pdfConverter = null,
        ITranslationRoomArtifactService? summaryVariants = null)
    {
        _unitOfWork = unitOfWork;
        _workspaceMemberDirectory = workspaceMemberDirectory;
        _documentWriter = documentWriter;
        _pdfConverter = pdfConverter;
        _summaryVariants = summaryVariants;
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
        var scopedRoom = _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(r => r.Id == roomId && r.DeletedAt == null && r.IsActive);

        var readable = await scopedRoom.AnyAsync(RoomReadAccess.IsReadableBy(userId, userEmail), ct);

        if (!readable)
        {
            // The list a reader arrived from admits a workspace Owner/Admin to every room in the
            // workspace (TranslationRoomService.BuildListableRoomsQueryAsync, and the same widening
            // is now in ListForWorkspaceAsync), so the read of one document has to agree — the
            // alternative is a library that shows an Admin a card and then reports the meeting does
            // not exist when they open it. CanAccessRoomAsync guards the room's artifacts the same
            // way, for the same reason.
            //
            // Asked second and only on failure, so the ordinary reader — host, participant,
            // invitee — never pays a gRPC hop, and a room whose workspace is unknown cannot reach
            // the directory at all.
            var workspaceId = await scopedRoom.Select(r => r.WorkspaceId).FirstOrDefaultAsync(ct);

            readable = workspaceId != Guid.Empty
                && await _workspaceMemberDirectory.IsOwnerOrAdminAsync(workspaceId, userId, ct);
        }

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
    public async Task<Result<MinutesTranslationDto>> GetTranslationAsync(
        Guid roomId,
        Guid userId,
        string? userEmail,
        string language,
        string? bearerToken,
        CancellationToken ct = default)
    {
        var wanted = LanguageHelper.NormalizeLanguageCode(language) ?? string.Empty;
        if (wanted.Length == 0)
        {
            return Result.Failure<MinutesTranslationDto>(
                "A language is required.", ErrorCodes.ValidationError);
        }

        // The document itself, through the read gate it already has. Reusing GetCurrentAsync
        // rather than restating the rule is what stops this becoming a second, looser door onto
        // an unsigned draft — that gate keeps a DRAFT with the people who can act on it, and a
        // translation of a draft must be no more reachable than the draft.
        var current = await GetCurrentAsync(roomId, userId, userEmail, ct);
        if (!current.IsSuccess)
        {
            return Result.Failure<MinutesTranslationDto>(current.Error!, current.ErrorCode);
        }

        var minutes = current.Value!;
        var content = JsonSerializer.Deserialize<MeetingMinutesContent>(minutes.Content, JsonOptions);
        if (content == null)
        {
            return Result.Failure<MinutesTranslationDto>(
                MeetingMinutesConstants.ErrorMinutesNotFound, ErrorCodes.NotFound);
        }

        // Already carried, because the room was interpreted into this language while it ran. The
        // stored one wins over anything this method could produce: it was written beside the
        // summary the document was drawn from, and it is what the DOCX has been printing.
        if (content.Translations != null
            && content.Translations.TryGetValue(wanted, out var carried)
            && carried is { Count: > 0 })
        {
            return Result<MinutesTranslationDto>.Success(new MinutesTranslationDto(
                wanted, carried, MinutesTranslationStatus.Ready, null));
        }

        // A DOCUMENT SOMEBODY EDITED CANNOT BE TRANSLATED FROM THE SUMMARY.
        //
        // The body below is rebuilt out of the meeting's summary, which is only a translation of
        // THIS document while the two still say the same thing. Once the secretary has corrected
        // a line, the summary no longer contains what the document contains — and the reader
        // would be shown prose the person who signed it never wrote, on the one artifact in this
        // product whose entire value is that a named person stood behind its words.
        //
        // Refused with its reason rather than silently omitted: "this cannot be translated
        // because a person edited it" is a fact the reader can act on, and an empty picker is not.
        if (minutes.EditCountVsDraft > 0)
        {
            return Result<MinutesTranslationDto>.Success(new MinutesTranslationDto(
                wanted,
                null,
                MinutesTranslationStatus.Unavailable,
                "This biên bản has been edited by its secretary, so it can only be read in the "
                + "languages it was drawn up in. A translation would show words nobody signed."));
        }

        if (_summaryVariants == null)
        {
            return Result<MinutesTranslationDto>.Success(new MinutesTranslationDto(
                wanted,
                null,
                MinutesTranslationStatus.Unavailable,
                "Reading this record in another language is not available on this deployment."));
        }

        var rendering = await _summaryVariants.GetOrQueueSummaryVariantAsync(
            roomId,
            userId,
            ReadTemplateKey(content),
            wanted,
            bearerToken,
            ct);

        if (!rendering.IsSuccess)
        {
            // The meeting has no summary to translate from — which is a real state for a document
            // drawn up and then had its summary artifact removed, and not something the reader
            // can fix. Said plainly instead of surfaced as a failure.
            return Result<MinutesTranslationDto>.Success(new MinutesTranslationDto(
                wanted,
                null,
                MinutesTranslationStatus.Unavailable,
                "This meeting no longer has a summary to translate the record from."));
        }

        if (rendering.Value!.Status != SummaryVariantStatus.Ready)
        {
            return Result<MinutesTranslationDto>.Success(new MinutesTranslationDto(
                wanted, null, MinutesTranslationStatus.Generating, null));
        }

        var sections = MeetingMinutesDrafter.SectionsFrom(rendering.Value.Content);

        // THE CHECK THAT MAKES THIS A TRANSLATION RATHER THAN A DIFFERENT DOCUMENT.
        //
        // The summary can be rewritten into another shape after minutes were drawn up, and then
        // the rebuilt body has different sections from the one on the page. Printing that beside
        // the document as "the same thing in Japanese" would be a claim of correspondence nobody
        // checked — the exact failure `pairByCitation` refuses at the line level, applied here at
        // the level of the document.
        //
        // Compared by KEY and not by count: a carried-over section exists in the document and is
        // deliberately absent from the rebuild (see SectionsFrom), so counts legitimately differ.
        var documentKeys = (content.Sections ?? new List<MinutesSection>())
            .Select(section => section.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        var rebuiltKeys = sections.Select(section => section.Key).ToHashSet(StringComparer.Ordinal);

        if (documentKeys.Count > 0 && !rebuiltKeys.IsSupersetOf(documentKeys))
        {
            return Result<MinutesTranslationDto>.Success(new MinutesTranslationDto(
                wanted,
                null,
                MinutesTranslationStatus.Unavailable,
                "The meeting's summary has changed shape since this biên bản was drawn up, so a "
                + "translation of it would not match the record."));
        }

        return Result<MinutesTranslationDto>.Success(new MinutesTranslationDto(
            wanted, sections, MinutesTranslationStatus.Ready, null));
    }

    /// <summary>
    /// Which summary shape this document was drawn from, so the rebuild asks for the same one.
    ///
    /// Read off the document's own section keys rather than stored: `MeetingMinutesContent` has
    /// never recorded a template, and inventing a column for it would make every existing
    /// document answer null. Standup, interview, demo and technical each declare sections no
    /// other template has, so their presence identifies the shape; anything else is General,
    /// which is what a document drawn before templates existed genuinely is.
    /// </summary>
    private static string ReadTemplateKey(MeetingMinutesContent content)
    {
        var keys = (content.Sections ?? new List<MinutesSection>())
            .Select(section => section.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (keys.Contains("progress") || keys.Contains("blockers")) return "standup";
        if (keys.Contains("strengths") || keys.Contains("concerns")) return "interview";
        if (keys.Contains("shown") || keys.Contains("objections")) return "demo";
        if (keys.Contains("problems") || keys.Contains("options")) return "technical";
        if (keys.Contains("narrative")) return "traceable";
        return "general";
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
        var workspaceRooms = _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(r => r.WorkspaceId == workspaceId && r.DeletedAt == null && r.IsActive);

        // A workspace Owner/Admin reads the whole workspace's archive, exactly as they do in the
        // rooms list.
        //
        // WHY THIS CLAUSE EXISTS. RoomReadAccess knows three ways into a room — host, participant,
        // invited by email — and deliberately does not model workspace role, because that answer
        // lives in WorkspaceService behind a gRPC call and cannot appear in an EF expression tree.
        // TranslationRoomService.BuildListableRoomsQueryAsync therefore adds the Owner/Admin
        // widening itself for the rooms list, which is what the Artifacts library's transcripts
        // and summaries arrive through. This list was written to RoomReadAccess alone, so ONE PAGE
        // answered the same question two ways: an Admin who hosted nothing saw every transcript
        // and every AI summary in the workspace, and zero minutes — the record that is hardest to
        // reach any other way, since a biên bản has no room panel a reader can guess at.
        //
        // The scope is the same one the rooms list widens to and no wider: one workspace's own
        // non-deleted rooms. Listing a minutes is not permission to act on it — signing, approving
        // and revising keep their own gates, and a DRAFT still stays with the people who can act
        // on it (see GetCurrentAsync).
        //
        // Host-or-participant is checked FIRST by asking the directory only when the caller is not
        // already inside the boundary... which cannot be expressed as a short-circuit here, because
        // the answer narrows a SET rather than a single room. So the directory is asked once per
        // request, and it never throws: an unreachable WorkspaceService answers false and the list
        // falls back to the ordinary read boundary rather than failing.
        var readsWholeWorkspace =
            await _workspaceMemberDirectory.IsOwnerOrAdminAsync(workspaceId, userId, ct);

        var readableRoomIds = (readsWholeWorkspace
                ? workspaceRooms
                : workspaceRooms.Where(RoomReadAccess.IsReadableBy(userId, userEmail)))
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
            // Case-insensitive substring over the document's IDENTITY — its number, its own
            // title, and the code of the meeting it belongs to.
            //
            // The title searched is the one inside `content`, NOT TranslationRoom.Title. Those two
            // diverge permanently the moment somebody renames a room: the document's front page
            // keeps the name the meeting was held under, because a biên bản records a moment and
            // retitling a signed one without a revision is the thing this module exists to
            // prevent. A library that searched the room's current name would list a card the
            // downloaded file does not agree with.
            //
            // The clause itself lives in MeetingMinutesRepository: reaching into a jsonb column
            // needs a provider-specific function, and this layer does not reference the vendor.
            //
            // Still NOT the document's PROSE. One key is extracted by name; the sections are not
            // scanned. The room history does not search artifact bodies either, and searching
            // inside minutes while transcripts and summaries beside them matched on title only
            // would make one kind of record behave unlike the rest of the page for no reason a
            // reader could see.
            //
            // Body search happens in the web, folded for diacritics, over the page it has already
            // loaded — every row here carries its whole Content, so there is nothing to fetch. The
            // real answer for the whole archive is one full-text index covering all three kinds,
            // which is a change to make once rather than three times.
            query = query.Where(
                _unitOfWork.MeetingMinutesRepository.MatchesSearch(request.Search));
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
                row.Minutes.ToDto(NameOf),
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
            //
            // This is what meeting_minutes_workspace_no_version_idx exists to admit. Under the
            // index it replaced, UNIQUE (workspace_id, minutes_no), the insert below raised 23505
            // on every call and an approved minutes could never be corrected at all.
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

        // Two statements, in this order, inside one transaction.
        //
        // meeting_minutes_one_current_per_room_idx is UNIQUE (translation_room_id) WHERE
        // is_current, so the old row must release the head pointer BEFORE the new row claims it.
        // Both writes in a single SaveChanges would leave that to EF's batch ordering, which is
        // not a guarantee: it happens to emit the UPDATE first here, and emits the INSERT first in
        // WorkspaceMinutesLibraryTests seeding the same shape. Relying on it would make the
        // difference between a revision and a 23505 an implementation detail of the ORM.
        //
        // The transaction is what keeps the intermediate state — a room whose minutes has no
        // current version — from being observable or from surviving a failure of the second write.
        await _unitOfWork.BeginTransactionAsync(ct);

        try
        {
            await _unitOfWork.SaveChangesAsync(ct);

            await _unitOfWork.MeetingMinutesRepository.AddAsync(revision, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            await _unitOfWork.CommitTransactionAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two people opened a revision of the same approved minutes at once. Both read v1,
            // both computed v2, and meeting_minutes_room_version_idx rejected the loser — the same
            // arrangement CreateDraftAsync relies on, and for the same reason: the alternative is
            // two rival v2 drafts of one record, with the second silently discarding the first
            // person's edits when it takes the head pointer.
            await _unitOfWork.RollbackTransactionAsync(ct);

            _logger.LogWarning(
                "Concurrent revision of minutes {MinutesNo} for room {RoomId}",
                approved.MinutesNo, roomId);
            return Result.Failure<MeetingMinutesDto>(
                MeetingMinutesConstants.ErrorRevisionAlreadyOpen, ErrorCodes.Conflict);
        }

        _logger.LogInformation(
            "Opened revision v{Version} of minutes {MinutesNo}", revision.Version, revision.MinutesNo);

        return Result.Success(await ToDtoAsync(revision, ct));
    }

    public async Task<Result<MinutesExportFile>> ExportAsync(
        Guid roomId, Guid userId, string? userEmail, string? template, string format,
        CancellationToken ct = default)
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

        return await RenderAsync(current.Value!, template, format, ct);
    }

    /// <summary>
    /// The document as a file, in the layout and the format asked for.
    ///
    /// The .docx is the original and the PDF is a CONVERSION of it, never a second layout: two
    /// renderers would agree on the day they were written and drift from then on, and a signed
    /// record whose Word copy and PDF copy differ is worse than having no PDF at all. The PDF of
    /// a template is therefore exactly the .docx of that template, printed.
    /// </summary>
    private async Task<Result<MinutesExportFile>> RenderAsync(
        MeetingMinutesDto minutes, string? template, string format, CancellationToken ct)
    {
        var content = TryReadContent(minutes.Content);
        if (content == null)
        {
            // The columns alone would render a file with a number, a signature block and nothing
            // between them — a document that looks complete and says nothing.
            return Result.Failure<MinutesExportFile>(
                MeetingMinutesConstants.ErrorContentUnreadable, ErrorCodes.InvalidState);
        }

        // Normalised here rather than at the controller so every caller — the HTTP endpoint
        // today, a scheduled circulation tomorrow — gets the same default and the same
        // tolerance of an unrecognised value.
        var docx = _documentWriter.WriteDocx(minutes, content, MinutesTemplates.Normalise(template));

        // Number first, then the meeting's own name: the number is the record's identity, but a
        // folder of BB-2026-0001, -0002, -0003 tells the person looking for last week's sprint
        // review nothing, and they open all three.
        if (!string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success(new MinutesExportFile(
                docx,
                MinutesFileName.For(minutes.MinutesNo, content.MeetingTitle, minutes.Version, "docx"),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
        }

        var pdf = _pdfConverter is { IsConfigured: true }
            ? await _pdfConverter.ToPdfAsync(docx, "minutes.docx", ct)
            : null;

        if (pdf == null)
        {
            // Said plainly rather than dressed as a server error: the Word file still downloads,
            // and that is what the caller should be told to do.
            return Result.Failure<MinutesExportFile>(
                MeetingMinutesConstants.ErrorSharePdfUnavailable, ErrorCodes.ServiceUnavailable);
        }

        return Result.Success(new MinutesExportFile(
            pdf,
            MinutesFileName.For(minutes.MinutesNo, content.MeetingTitle, minutes.Version, "pdf"),
            "application/pdf"));
    }

    // ------------------------------------------------------------------ sharing

    public async Task<Result<MinutesShareDto>> GetOrCreateShareAsync(
        Guid roomId, Guid userId, CancellationToken ct = default)
    {
        var gate = await AuthorizeManageAsync(roomId, userId, ct);
        if (!gate.IsSuccess)
        {
            return Result.Failure<MinutesShareDto>(
                gate.Error ?? MeetingMinutesConstants.ErrorRoomNotFound, gate.ErrorCode);
        }

        var link = await _unitOfWork.MeetingMinutesShareRepository.GetByRoomIdAsync(roomId, ct);
        if (link == null)
        {
            // INVITED_ONLY on creation. Opening the share dialog must never be the act that
            // publishes a document.
            var now = DateTime.UtcNow;
            link = new MeetingMinutesShareLink
            {
                // v7 like every other id this service mints: time-ordered, so rows written
                // together stay together in the index.
                Id = Guid.CreateVersion7(),
                TranslationRoomId = roomId,
                WorkspaceId = gate.Value!.WorkspaceId,
                Token = MinutesShareAccess.NewToken(),
                AccessMode = MeetingMinutesConstants.ShareModeInvitedOnly,
                AllowDownload = true,
                CreatedAt = now,
                CreatedBy = userId,
                UpdatedAt = now,
                UpdatedBy = userId
            };

            await _unitOfWork.MeetingMinutesShareRepository.AddLinkAsync(link, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        return Result.Success(await ToShareDtoAsync(link, ct));
    }

    public async Task<Result<MinutesShareDto>> UpdateShareAsync(
        Guid roomId, Guid userId, UpdateMinutesShareRequest request, CancellationToken ct = default)
    {
        if (request.AccessMode != null && !MinutesShareAccess.IsKnownMode(request.AccessMode))
        {
            // Guarded here rather than trusted: an unrecognised mode in the column reads back as
            // "not public", and a link nobody can open looks like data loss.
            return Result.Failure<MinutesShareDto>(
                MeetingMinutesConstants.ErrorShareModeUnknown, ErrorCodes.ValidationError);
        }

        var existing = await GetOrCreateShareAsync(roomId, userId, ct);
        if (!existing.IsSuccess) return existing;

        var link = await _unitOfWork.MeetingMinutesShareRepository.GetByRoomIdAsync(roomId, ct);
        if (link == null)
        {
            return Result.Failure<MinutesShareDto>(
                MeetingMinutesConstants.ErrorShareLinkNotFound, ErrorCodes.NotFound);
        }

        var widened = request.AccessMode == MeetingMinutesConstants.ShareModeAnyoneWithLink
            && link.AccessMode != MeetingMinutesConstants.ShareModeAnyoneWithLink;

        if (request.AccessMode != null) link.AccessMode = request.AccessMode;
        if (request.AllowDownload.HasValue) link.AllowDownload = request.AllowDownload.Value;
        if (request.ExpiresAt.HasValue) link.ExpiresAt = request.ExpiresAt;

        // Only CHOOSING A MODE brings a revoked link back, and it comes back on the token the
        // revoke already rotated to, so the URL somebody was sent stays dead. Toggling downloads
        // or an expiry must not resurrect sharing as a side effect: a host who has just killed a
        // link and then adjusts a setting has not asked to publish the document again.
        if (request.AccessMode != null)
        {
            link.RevokedAt = null;
            link.RevokedBy = null;
        }

        link.UpdatedAt = DateTime.UtcNow;
        link.UpdatedBy = userId;

        await _unitOfWork.SaveChangesAsync(ct);

        if (widened)
        {
            // Logged at warning because it is the one action here that cannot be undone for
            // anybody who already has the URL. The product warns the person; this is the record.
            _logger.LogWarning(
                "Minutes for room {RoomId} were opened to anyone with the link by {UserId}",
                roomId, userId);
        }

        return Result.Success(await ToShareDtoAsync(link, ct));
    }

    public async Task<Result<MinutesShareDto>> RevokeShareAsync(
        Guid roomId, Guid userId, CancellationToken ct = default)
    {
        var gate = await AuthorizeManageAsync(roomId, userId, ct);
        if (!gate.IsSuccess)
        {
            return Result.Failure<MinutesShareDto>(
                gate.Error ?? MeetingMinutesConstants.ErrorRoomNotFound, gate.ErrorCode);
        }

        var link = await _unitOfWork.MeetingMinutesShareRepository.GetByRoomIdAsync(roomId, ct);
        if (link == null)
        {
            return Result.Failure<MinutesShareDto>(
                MeetingMinutesConstants.ErrorShareLinkNotFound, ErrorCodes.NotFound);
        }

        // The token is replaced, not flagged. Revoke means the URL in somebody's inbox stops
        // working, and a row that still holds the old string is one bug away from honouring it.
        link.Token = MinutesShareAccess.NewToken();
        link.RevokedAt = DateTime.UtcNow;
        link.RevokedBy = userId;
        link.UpdatedAt = DateTime.UtcNow;
        link.UpdatedBy = userId;

        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("Share link for minutes of room {RoomId} revoked by {UserId}", roomId, userId);

        return Result.Success(await ToShareDtoAsync(link, ct));
    }

    public async Task<Result<MinutesShareDto>> AddSharePersonAsync(
        Guid roomId, Guid userId, string email, CancellationToken ct = default)
    {
        if (!LooksLikeEmail(email))
        {
            return Result.Failure<MinutesShareDto>(
                MeetingMinutesConstants.ErrorShareEmailInvalid, ErrorCodes.ValidationError);
        }

        // Adding somebody creates the link if it does not exist yet: "share with Nhi" is one act
        // to the person doing it, and asking them to press two buttons is asking them to forget
        // the second.
        var share = await GetOrCreateShareAsync(roomId, userId, ct);
        if (!share.IsSuccess) return share;

        var repository = _unitOfWork.MeetingMinutesShareRepository;
        if (!await repository.HasGrantAsync(roomId, email, ct))
        {
            await repository.AddGrantAsync(
                new MeetingMinutesShareGrant
                {
                    Id = Guid.CreateVersion7(),
                    TranslationRoomId = roomId,
                    Email = email,
                    GrantedBy = userId,
                    CreatedAt = DateTime.UtcNow
                },
                ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        var link = await repository.GetByRoomIdAsync(roomId, ct);
        return link == null
            ? Result.Failure<MinutesShareDto>(
                MeetingMinutesConstants.ErrorShareLinkNotFound, ErrorCodes.NotFound)
            : Result.Success(await ToShareDtoAsync(link, ct));
    }

    public async Task<Result<MinutesShareDto>> RemoveSharePersonAsync(
        Guid roomId, Guid userId, string email, CancellationToken ct = default)
    {
        var gate = await AuthorizeManageAsync(roomId, userId, ct);
        if (!gate.IsSuccess)
        {
            return Result.Failure<MinutesShareDto>(
                gate.Error ?? MeetingMinutesConstants.ErrorRoomNotFound, gate.ErrorCode);
        }

        var repository = _unitOfWork.MeetingMinutesShareRepository;
        await repository.RemoveGrantAsync(roomId, email, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var link = await repository.GetByRoomIdAsync(roomId, ct);
        return link == null
            ? Result.Failure<MinutesShareDto>(
                MeetingMinutesConstants.ErrorShareLinkNotFound, ErrorCodes.NotFound)
            : Result.Success(await ToShareDtoAsync(link, ct));
    }

    public async Task<Result<SharedMinutesDto>> GetSharedAsync(
        string token, Guid? viewerUserId, string? viewerEmail, CancellationToken ct = default)
    {
        var opened = await OpenLinkAsync(token, viewerUserId, viewerEmail, ct);
        if (!opened.IsSuccess)
        {
            return Result.Failure<SharedMinutesDto>(opened.Error!, opened.ErrorCode);
        }

        var link = opened.Value!;
        var minutes = await _unitOfWork.MeetingMinutesRepository
            .GetCurrentByRoomIdAsync(link.TranslationRoomId, ct);

        if (minutes == null)
        {
            return Result.Failure<SharedMinutesDto>(
                MeetingMinutesConstants.ErrorMinutesNotFound, ErrorCodes.NotFound);
        }

        // The link does not outrank the document's own lifecycle: a draft nobody has signed is not
        // published, and a share link must not be the way one gets published. InvalidState rather
        // than Forbidden — the holder's link is fine, the document is not ready — so the page can
        // say "not signed yet" instead of "you are not allowed".
        if (!MinutesShareAccess.IsServable(minutes.Status))
        {
            return Result.Failure<SharedMinutesDto>(
                MeetingMinutesConstants.ErrorMinutesNotPublished, ErrorCodes.InvalidState);
        }

        return Result.Success(new SharedMinutesDto(
            await ToDtoAsync(minutes, ct), link.AccessMode, link.AllowDownload));
    }

    public async Task<Result<MinutesExportFile>> ExportSharedAsync(
        string token, Guid? viewerUserId, string? viewerEmail, string? template, string format,
        CancellationToken ct = default)
    {
        var opened = await OpenLinkAsync(token, viewerUserId, viewerEmail, ct);
        if (!opened.IsSuccess)
        {
            return Result.Failure<MinutesExportFile>(opened.Error!, opened.ErrorCode);
        }

        var link = opened.Value!;
        if (!link.AllowDownload)
        {
            return Result.Failure<MinutesExportFile>(
                MeetingMinutesConstants.ErrorShareDownloadDisabled, ErrorCodes.Forbidden);
        }

        var minutes = await _unitOfWork.MeetingMinutesRepository
            .GetCurrentByRoomIdAsync(link.TranslationRoomId, ct);

        if (minutes == null)
        {
            return Result.Failure<MinutesExportFile>(
                MeetingMinutesConstants.ErrorMinutesNotFound, ErrorCodes.NotFound);
        }

        // Same rule as reading it on screen. A download that worked while the page refused would
        // be the leak with an extra step.
        if (!MinutesShareAccess.IsServable(minutes.Status))
        {
            return Result.Failure<MinutesExportFile>(
                MeetingMinutesConstants.ErrorMinutesNotPublished, ErrorCodes.InvalidState);
        }

        return await RenderAsync(await ToDtoAsync(minutes, ct), template, format, ct);
    }

    /// <summary>
    /// Resolve a token to the link it names, or to the reason it does not open.
    ///
    /// The one place the sharing rule is applied, so a reader and a downloader can never disagree
    /// about who is allowed in.
    /// </summary>
    private async Task<Result<MeetingMinutesShareLink>> OpenLinkAsync(
        string token, Guid? viewerUserId, string? viewerEmail, CancellationToken ct)
    {
        var link = await _unitOfWork.MeetingMinutesShareRepository.GetByTokenAsync(token, ct);

        // A deleted meeting takes its link with it. Deletion is soft here, so nothing about the
        // row would have stopped a public URL from carrying on serving the minutes of a meeting
        // the host had already removed — and "delete the meeting" has to mean that too, or the
        // deletion is only true inside the app.
        if (link != null && !await RoomIsLiveAsync(link.TranslationRoomId, ct))
        {
            link = null;
        }

        // Named on the share list, or already entitled to the minutes by the ordinary room rules:
        // a restricted link widens who may read, it never narrows it for the people who were
        // at the meeting.
        var mayRead = false;
        if (link != null && viewerUserId.HasValue)
        {
            mayRead = !string.IsNullOrWhiteSpace(viewerEmail)
                && await _unitOfWork.MeetingMinutesShareRepository
                    .HasGrantAsync(link.TranslationRoomId, viewerEmail!, ct);

            if (!mayRead)
            {
                mayRead = await CanReadRoomAsync(link.TranslationRoomId, viewerUserId.Value, viewerEmail, ct);
            }
        }

        return MinutesShareAccess.Decide(link, viewerUserId.HasValue, mayRead, DateTime.UtcNow) switch
        {
            ShareDecision.Granted => Result.Success(link!),
            ShareDecision.SignInRequired => Result.Failure<MeetingMinutesShareLink>(
                MeetingMinutesConstants.ErrorUnauthorizedRead, ErrorCodes.Unauthorized),
            ShareDecision.Forbidden => Result.Failure<MeetingMinutesShareLink>(
                MeetingMinutesConstants.ErrorUnauthorizedRead, ErrorCodes.Forbidden),
            _ => Result.Failure<MeetingMinutesShareLink>(
                MeetingMinutesConstants.ErrorShareLinkNotFound, ErrorCodes.NotFound)
        };
    }

    /// <summary>Whether the meeting behind a link still exists — the same test every other read makes.</summary>
    private async Task<bool> RoomIsLiveAsync(Guid roomId, CancellationToken ct) =>
        await _unitOfWork.TranslationRoomRepository
            .Query()
            .AnyAsync(r => r.Id == roomId && r.DeletedAt == null && r.IsActive, ct);

    private async Task<bool> CanReadRoomAsync(
        Guid roomId, Guid userId, string? userEmail, CancellationToken ct)
    {
        return await _unitOfWork.TranslationRoomRepository
            .Query()
            .Where(r => r.Id == roomId && r.DeletedAt == null && r.IsActive)
            .AnyAsync(RoomReadAccess.IsReadableBy(userId, userEmail), ct);
    }

    private async Task<MinutesShareDto> ToShareDtoAsync(
        MeetingMinutesShareLink link, CancellationToken ct)
    {
        var people = await _unitOfWork.MeetingMinutesShareRepository
            .GetGrantsAsync(link.TranslationRoomId, ct);

        // A revoked link has no address. Returning the rotated token would put a dead URL on
        // screen that looks exactly like a live one.
        var live = link.RevokedAt == null;

        return new MinutesShareDto(
            live ? link.Token : string.Empty,
            live ? $"{_frontendBaseUrl}/minutes/shared/{link.Token}" : string.Empty,
            link.AccessMode,
            link.AllowDownload,
            link.ExpiresAt,
            link.RevokedAt,
            people.Select(grant => new MinutesSharePersonDto(grant.Email, grant.CreatedAt)).ToList());
    }

    /// <summary>
    /// Enough of a check to catch a typo, and no more. Address validation beyond this rejects real
    /// addresses, and the invitation is not a security boundary — the grant is matched on exact
    /// text, so a mistyped address simply never matches anybody.
    /// </summary>
    private static bool LooksLikeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320) return false;

        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@');
        return at > 0
            && at < trimmed.Length - 1
            && trimmed.IndexOf('@', at + 1) < 0
            && !trimmed.Contains(' ');
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

    /// <summary>
    /// Loads what the shaping needs and hands it to <see cref="MeetingMinutesMapper"/>. The read
    /// is the service's job; the shape of the DTO is not, and lived here only because the roster
    /// lookup made the mapping look asynchronous.
    /// </summary>
    private async Task<MeetingMinutesDto> ToDtoAsync(MeetingMinutes minutes, CancellationToken ct)
    {
        var participants = await _unitOfWork.TranslationRoomParticipantRepository
            .GetByRoomIdAsync(minutes.TranslationRoomId, ct);

        return minutes.ToDto(participantId => participantId == null
            ? null
            : participants?.FirstOrDefault(p => p.Id == participantId)?.DisplayName);
    }
}
