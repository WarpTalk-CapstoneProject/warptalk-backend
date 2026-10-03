using WarpTalk.Shared.PlatformSettings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceDocumentService : IWorkspaceDocumentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDocumentAccessEvaluator _accessEvaluator;
    private readonly IWorkspaceDocumentEventPublisher _eventPublisher;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceUrlProvider _urlProvider;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly IWorkspaceDocumentStorage _storage;
    private readonly IDocumentTextExtractor _textExtractor;
    private readonly IKnowledgeChunkWriter _chunkWriter;
    private readonly ILogger<WorkspaceDocumentService> _logger;

    private readonly IPlatformSettings? _platformSettings;

    // Optional so the constructor stays source-compatible; when either is missing, a path that
    // needs a scan fails closed (does not index) instead of indexing unscanned text.
    private readonly IDocumentSecurityScanner? _securityScanner;
    private readonly IAiPolicyResolver? _policyResolver;

    public WorkspaceDocumentService(
        IUnitOfWork unitOfWork,
        IDocumentAccessEvaluator accessEvaluator,
        IWorkspaceDocumentEventPublisher eventPublisher,
        IAuthIdentityClient authIdentity,
        IWorkspaceUrlProvider urlProvider,
        ITranslationRoomClient translationRoomClient,
        IWorkspaceDocumentStorage storage,
        IDocumentTextExtractor textExtractor,
        IKnowledgeChunkWriter chunkWriter,
        ILogger<WorkspaceDocumentService> logger,
        IPlatformSettings? platformSettings = null,
        IDocumentSecurityScanner? securityScanner = null,
        IAiPolicyResolver? policyResolver = null)
    {
        _securityScanner = securityScanner;
        _policyResolver = policyResolver;
        _platformSettings = platformSettings;
        _unitOfWork = unitOfWork;
        _accessEvaluator = accessEvaluator;
        _eventPublisher = eventPublisher;
        _authIdentity = authIdentity;
        _urlProvider = urlProvider;
        _translationRoomClient = translationRoomClient;
        _storage = storage;
        _textExtractor = textExtractor;
        _chunkWriter = chunkWriter;
        _logger = logger;
    }

    /// <summary>
    /// The upload limit for this workspace — platform setting limits.document_upload_mb, resolved
    /// workspace override, then its plan's, then the platform value — read on every upload so a
    /// change in /admin/settings applies to the next file. Null when the file fits.
    /// </summary>
    public async Task<string?> UploadTooLargeAsync(Guid workspaceId, long length, CancellationToken ct = default)
    {
        var limitMb = WorkspaceDocumentConstants.DefaultMaxUploadMb;
        if (_platformSettings is not null)
        {
            string? planSlug = null;
            try
            {
                planSlug = (await _unitOfWork.WorkspaceEntitlementSnapshotRepository.GetForWorkspaceAsync(workspaceId, ct))?.PlanSlug;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A plan override is a refinement: without the snapshot the platform value still applies.
                _logger.LogDebug(ex, "Upload limit for {WorkspaceId} resolved without its plan.", workspaceId);
            }

            limitMb = await _platformSettings.GetInt32Async(
                PlatformSettingsCatalog.DocumentUploadMb,
                WorkspaceDocumentConstants.DefaultMaxUploadMb,
                new SettingContext(workspaceId, planSlug),
                ct);
        }

        return length > limitMb * 1024L * 1024L
            ? $"The file is larger than this workspace's {limitMb} MB upload limit."
            : null;
    }

    /// <summary>
    /// Refuse every document operation on a workspace that is suspended or deleted.
    /// </summary>
    /// <remarks>
    /// THE HOLE THIS PLUGS. Upload and List asked this question; the other twelve endpoints did
    /// not, and <see cref="IDocumentAccessEvaluator"/> never loads the workspace at all. Deletion
    /// happened to be safe because both delete paths stamp RemovedAt on every member, so the
    /// membership lookups fail closed. SUSPENSION is not: AdminWorkspaceService.ChangeLifecycleAsync
    /// flips IsActive and leaves every membership row live.
    ///
    /// So a suspended workspace returned 404 from the document LIST while every by-id route —
    /// get, download, extracted-text read and write, patch, approve, delete, archive, restore and
    /// all three policy routes — kept working for anyone holding a document id. Suspension is what
    /// an admin reaches for on non-payment, abuse or legal hold, and it did not stop the documents
    /// leaving.
    ///
    /// It lives here rather than in the evaluator for two reasons: the evaluator is injected into
    /// this service and nothing else, so this is the real choke point; and the list path evaluates
    /// N documents per call, which would turn one workspace lookup into N.
    /// </remarks>
    /// <summary>
    /// Pre-load the meetings behind a set of documents, for an External member.
    /// </summary>
    /// <remarks>
    /// Only External members need it: they reach a meeting-sourced document through the
    /// participant-plus-grace-period exception in <see cref="IDocumentAccessEvaluator"/>, and
    /// answering that per document would be two gRPC round trips each. Internal members never
    /// take that branch, so the caches stay null and nothing is fetched.
    ///
    /// Shared by the document list and the AI-retrievable id list rather than copied into the
    /// second one. Both ask the evaluator the same question over the same documents; a private
    /// copy of the fetching would be a second place for the External rule to go quietly wrong.
    /// </remarks>
    private async Task<(Dictionary<Guid, TranslationRoomDto?>?, Dictionary<Guid, List<TranslationRoomParticipantDto>>?)> BuildMeetingCachesAsync(
        WorkspaceMember member,
        IEnumerable<WorkspaceDocument> documents,
        CancellationToken ct)
    {
        if (!string.Equals(member.MembershipType, MembershipType.External.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var meetingIds = documents
            .Where(d => string.Equals(d.SourceType, WorkspaceDocumentConstants.SourceTypeMeeting, StringComparison.OrdinalIgnoreCase) && d.SourceId.HasValue)
            .Select(d => d.SourceId!.Value)
            .Distinct()
            .ToList();

        if (meetingIds.Count == 0)
        {
            return (null, null);
        }

        var roomCache = new Dictionary<Guid, TranslationRoomDto?>();
        var participantsCache = new Dictionary<Guid, List<TranslationRoomParticipantDto>>();

        var roomTasks = meetingIds.Select(async id =>
        {
            var room = await _translationRoomClient.GetTranslationRoomAsync(id, ct);
            return (id, room);
        }).ToList();

        var participantTasks = meetingIds.Select(async id =>
        {
            var participants = await _translationRoomClient.GetParticipantsAsync(id, ct);
            return (id, participants);
        }).ToList();

        await Task.WhenAll(roomTasks.Cast<Task>().Concat(participantTasks.Cast<Task>()));

        foreach (var task in roomTasks)
        {
            var res = await task;
            roomCache[res.id] = res.room;
        }

        foreach (var task in participantTasks)
        {
            var res = await task;
            participantsCache[res.id] = res.participants;
        }

        return (roomCache, participantsCache);
    }

    /// <summary>
    /// The documents this caller may have the assistant answer from.
    /// </summary>
    /// <remarks>
    /// WHY THIS ENDPOINT EXISTS AT ALL.
    ///
    /// `ai_retrieval` has been one of three document permissions since the ACL was written.
    /// DocumentAccessEvaluator implements it in full — status, retention, ingestion, AiEligible,
    /// then the per-subject policies and the hierarchy above them — and the web can grant and
    /// revoke it per user and per role. Nothing ever asked it. Every production call site passed
    /// `view` or `download`; the only place the constant reached the evaluator was a unit test.
    ///
    /// Meanwhile the assistant's semantic search could not consult a document's ACL at all, so it
    /// excluded documents wholesale for anyone who was not an Owner or Admin. Safe, and blunt:
    /// members lost every document answer they were entitled to, and the permission the UI
    /// offered them changed nothing either way.
    ///
    /// This is the seam that joins the two. The AI path asks this endpoint AS THE CALLER, gets
    /// the ids it may retrieve, and scopes the vector query to them — the same shape the meeting
    /// allowlist already uses. The authorization stays here, in the one evaluator that knows the
    /// rules; Python never re-implements an ACL.
    ///
    /// NO RE-INDEX IS NEEDED, contrary to what the phase-2 note in search_worker.py assumed. That
    /// note was about putting the ACL itself into the vector payload. An allowlist does not need
    /// it: filtering on a document id only needs the document id, and RedisEmbeddingIndexPublisher
    /// has always written `source_id` for document chunks.
    ///
    /// Capped, and the cap is reported. A workspace with more retrievable documents than the cap
    /// would otherwise have the tail silently excluded, which reads to the asker as "the
    /// assistant does not know about that document" — the exact failure this whole change is
    /// trying to stop being invisible.
    /// </remarks>
    public async Task<Result<AiRetrievableDocumentsDto>> ListAiRetrievableDocumentIdsAsync(
        Guid workspaceId,
        Guid userId,
        int limit,
        CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<AiRetrievableDocumentsDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure<AiRetrievableDocumentsDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var effectiveLimit = limit <= 0
                ? WorkspaceDocumentConstants.DefaultAiRetrievableIdLimit
                : Math.Min(limit, WorkspaceDocumentConstants.MaxAiRetrievableIdLimit);

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);

            var allPolicies = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.FindAsync(
                p => p.WorkspaceId == workspaceId, "", ct);
            var policiesByDoc = allPolicies.ToLookup(p => p.DocumentId);

            // AiEligible is the cheap pre-filter, and it is the one the index itself tracks: a
            // document that has never been embedded cannot be returned by a vector search, so
            // evaluating the rest of the ACL over it would be work for an id nobody can match.
            // The evaluator still re-checks it — this narrows the set, it does not decide it.
            var documents = await _unitOfWork.WorkspaceDocumentRepository.FindAsync(
                d => d.WorkspaceId == workspaceId && d.DeletedAt == null && d.AiEligible, "", ct);

            var ordered = documents.OrderByDescending(d => d.UpdatedAt == default ? d.CreatedAt : d.UpdatedAt).ToList();
            var (roomCache, participantsCache) = await BuildMeetingCachesAsync(member, ordered, ct);

            var ids = new List<Guid>();
            var truncated = false;
            foreach (var doc in ordered)
            {
                if (ids.Count >= effectiveLimit)
                {
                    truncated = true;
                    break;
                }

                var accessResult = await _accessEvaluator.EvaluateAccessAsync(
                    userId,
                    workspaceId,
                    doc,
                    WorkspaceDocumentPermissions.AiRetrieval,
                    member,
                    roleName,
                    policiesByDoc[doc.Id],
                    roomCache,
                    participantsCache,
                    ct);

                if (accessResult.IsSuccess)
                {
                    ids.Add(doc.Id);
                }
            }

            return Result.Success(new AiRetrievableDocumentsDto(ids, truncated));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing AI-retrievable documents. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<AiRetrievableDocumentsDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    private async Task<bool> IsWorkspaceOperationalAsync(Guid workspaceId, CancellationToken ct)
    {
        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        return workspace is not null && workspace.IsOperational();
    }

    public async Task<Result<UploadDocumentOutcomeDto>> UploadDocumentAsync(Guid workspaceId, UploadDocumentApiRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<UploadDocumentOutcomeDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            if (await UploadTooLargeAsync(workspaceId, request.File.Length, ct) is { } tooLarge)
            {
                return Result.Failure<UploadDocumentOutcomeDto>(tooLarge, ErrorCodes.ValidationError);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure<UploadDocumentOutcomeDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();

            // EVERY CHECK BELOW RUNS BEFORE A SINGLE BYTE REACHES STORAGE. The old order wrote the
            // encrypted blob first and let Postgres be the validator, so an over-long name came
            // back as a 500 with an orphaned encrypted file already on disk.
            //
            // Trimmed, not merely measured. " Report " and "Report" are the same document to the
            // person naming it, and storing the padded form made the list, the search and the name
            // shown in the UI disagree with each other.
            var name = (request.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return Result.Failure<UploadDocumentOutcomeDto>("Document name is required.", ErrorCodes.ValidationError);
            }

            if (name.Length > WorkspaceDocumentConstants.MaxDocumentNameLength)
            {
                return Result.Failure<UploadDocumentOutcomeDto>(
                    $"Document name must be {WorkspaceDocumentConstants.MaxDocumentNameLength} characters or fewer. This one is {name.Length}.",
                    ErrorCodes.ValidationError);
            }

            var sourceType = WorkspaceDocumentHelper.NormalizeSourceType(request.SourceType);
            if (sourceType is null)
            {
                return Result.Failure<UploadDocumentOutcomeDto>(
                    $"Unsupported source type. Allowed values are: {WorkspaceDocumentHelper.SupportedSourceTypes}.",
                    ErrorCodes.ValidationError);
            }

            var duplicateStrategy = WorkspaceDocumentHelper.NormalizeDuplicateStrategy(request.DuplicateStrategy);
            if (duplicateStrategy is null)
            {
                return Result.Failure<UploadDocumentOutcomeDto>(
                    $"Unsupported duplicate strategy. Allowed values are: {WorkspaceDocumentHelper.SupportedDuplicateStrategies}.",
                    ErrorCodes.ValidationError);
            }

            var docId = Guid.NewGuid();
            var extension = WorkspaceDocumentHelper.NormalizeExtension(System.IO.Path.GetExtension(request.File.FileName));
            if (!WorkspaceDocumentHelper.IsSupportedUploadExtension(extension))
            {
                var allowed = string.Join(", ", WorkspaceDocumentConstants.SupportedUploadExtensions);
                return Result.Failure<UploadDocumentOutcomeDto>($"Unsupported file type. Allowed file types are: {allowed}.", ErrorCodes.ValidationError);
            }

            // Refused at the boundary rather than stored and misread later. Every policy check
            // downstream — access evaluation and index eligibility — asks IsRestricted(), which is
            // an equality test against "restricted". So an unrecognised label is silently a
            // NON-restricted document wearing a confidential-looking word in the UI, and it is
            // embedded into the vector store and answerable by the assistant.
            var confidentiality = WorkspaceDocumentHelper.NormalizeConfidentialityLevel(request.ConfidentialityLevel);
            if (!string.IsNullOrWhiteSpace(request.ConfidentialityLevel) && confidentiality is null)
            {
                return Result.Failure<UploadDocumentOutcomeDto>(
                    $"Unsupported confidentiality level. Allowed values are: {WorkspaceDocumentHelper.SupportedConfidentialityLevels}.",
                    ErrorCodes.ValidationError);
            }

            var contentResult = await ReadAndValidateContentAsync(request.File, extension, ct);
            if (!contentResult.IsSuccess || contentResult.Value is null)
            {
                return Result.Failure<UploadDocumentOutcomeDto>(contentResult.Error ?? "Invalid file.", contentResult.ErrorCode);
            }

            var content = contentResult.Value;
            var contentHash = DocumentContentHelper.ComputeSha256(content);

            if (!string.Equals(duplicateStrategy, WorkspaceDocumentConstants.DuplicateStrategies.CreateNew, StringComparison.Ordinal))
            {
                var settled = await ResolveDuplicateAsync(workspaceId, userId, contentHash, duplicateStrategy, request, ct);
                if (settled is not null)
                {
                    return settled;
                }
            }

            var storageKey = WorkspaceDocumentHelper.GenerateStorageKey(workspaceId, docId, extension);

            var status = isOwnerOrAdmin
                ? WorkspaceDocumentStatus.@public
                : WorkspaceDocumentStatus.pending_approval;

            var effectiveIsAiAllowed = request.IsAiAllowed && WorkspaceDocumentHelper.IsAiReadableExtension(extension);

            WorkspaceDocumentIngestionStatus ingestionStatus;
            if (!effectiveIsAiAllowed)
            {
                ingestionStatus = WorkspaceDocumentIngestionStatus.skipped;
            }
            else
            {
                ingestionStatus = isOwnerOrAdmin
                    ? WorkspaceDocumentIngestionStatus.pending
                    : WorkspaceDocumentIngestionStatus.awaiting_approval;
            }

            var aiEligible = false; // Initial state: false until ingestion completes or if IsAiAllowed == false

            var document = request.ToEntity(docId, workspaceId, userId, storageKey, _storage.StorageProviderName, status, ingestionStatus, aiEligible, contentHash: contentHash);
            document.IsAiAllowed = effectiveIsAiAllowed;
            // The CANONICAL values, not the caller's spelling — "Restricted " and "RESTRICTED"
            // both mean restricted, and storing either verbatim would make IsRestricted() false
            // for one of them. Same reasoning for the name and the source type: the mapper copies
            // the request through verbatim, so the normalised forms are applied here.
            document.ConfidentialityLevel = confidentiality ?? WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel;

            // The verdict follows the BYTES, not the row. A scan that found PII/DLP labels one
            // document restricted; a fresh row for the same file used to start clean and be judged
            // again from scratch — by a model, for anything the regexes cannot see.
            if (await HasRestrictedTwinAsync(workspaceId, contentHash, null, ct))
            {
                document.ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel;
            }

            document.Name = name;
            document.SourceType = sourceType;

            // Save the document content securely to physical storage (AES-256 + HMAC-SHA512) before DB transaction.
            // The already-read copy, not a second OpenReadStream: the signature check and the hash
            // were computed from THESE bytes, and re-reading the request stream would leave a gap
            // in which the validated payload and the stored payload are not provably the same.
            using (var stream = new MemoryStream(content, writable: false))
            {
                await _storage.SaveDocumentContentAsync(document, stream, ct);
            }

            try
            {
                await _unitOfWork.WorkspaceDocumentRepository.AddAsync(document, ct);
                if (isOwnerOrAdmin && effectiveIsAiAllowed)
                {
                    await _eventPublisher.PublishDocumentUploadedAsync(
                        document.Id,
                        workspaceId,
                        document.StorageKey,
                        document.FileName,
                        document.FileExtension,
                        userId,
                        document.ConfidentialityLevel,
                        ct);
                }
                await _unitOfWork.SaveChangesAsync(ct);
            }
            catch
            {
                // The blob is already on disk but has no DB row to reference it — clean it up
                // rather than leaving an orphaned encrypted file behind.
                await _storage.DeleteDocumentContentAsync(document, ct);
                throw;
            }

            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                status == WorkspaceDocumentStatus.pending_approval
                    ? WorkspaceDocumentConstants.LifecycleEvents.PendingApproval
                    : WorkspaceDocumentConstants.LifecycleEvents.Created,
                document.UpdatedAt,
                userId,
                ct);

            await _unitOfWork.AuditAsync(document.Id, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.UploadDocument, new { document.Name, document.ConfidentialityLevel }, _logger, ct);

            var downloadUrl = _urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id);
            return Result.Success(new UploadDocumentOutcomeDto(UploadDocumentOutcomeDto.Created, document.ToDto(downloadUrl)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while uploading document. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<UploadDocumentOutcomeDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Scans edited extracted text under the document's effective AI policy and indexes it only if
    /// it is clean. Returns whether an index request was published.
    /// </summary>
    /// <remarks>
    /// Fails closed three ways: no scanner wired, the scan throws, or it finds something. A finding
    /// does what the upload guardrail does — label the document restricted and pull its vectors.
    /// The masked text is not indexed here: the consumer indexes it and the result processor then
    /// purges it because the row is restricted, so publishing it would only be wasted work.
    /// </remarks>
    private async Task<bool> ScanThenIndexEditedTextAsync(WorkspaceDocument document, string text, CancellationToken ct)
    {
        if (_securityScanner is null || _policyResolver is null)
        {
            _logger.LogWarning(
                "Edited text of document {DocumentId} was saved but not indexed: no security scanner is available.",
                document.Id);
            return false;
        }

        DocumentSecurityScanResult scan;
        try
        {
            var policy = await _policyResolver.ResolvePolicySettingsAsync(_unitOfWork, document, ct);
            scan = await _securityScanner.ScanAsync(text, policy.PiiEnabled, policy.DlpEnabled, policy.KeywordsBlacklist, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Security scan of edited text failed for document {DocumentId}; not indexing.", document.Id);
            return false;
        }

        if (scan.PiiDetected || scan.DlpDetected)
        {
            document.ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel;
            document.AiEligible = false;
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
            document.IngestionFailureReason = scan.DlpDetected
                ? WorkspaceDocumentIngestionFailureReasons.DlpDetected
                : null;
            document.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);
            await _eventPublisher.PublishDocumentDeletedAsync(document.Id, document.WorkspaceId, ct);
            return false;
        }

        await _eventPublisher.PublishEmbeddingIndexRequestAsync(document.Id, document.WorkspaceId, text, true, ct);
        return true;
    }

    /// <summary>
    /// Has any other row in this workspace with the same bytes already been labelled restricted?
    /// </summary>
    /// <remarks>
    /// Soft-deleted rows count: deleting a flagged document and uploading it again is the obvious
    /// way round a label that lives only on the row. An Owner/Admin can still relabel the new row,
    /// which re-scans it. The label is read as written, so a manual "restricted" is inherited too —
    /// the same file was already declared confidential once.
    /// </remarks>
    private Task<bool> HasRestrictedTwinAsync(Guid workspaceId, string? contentHash, Guid? excludeDocumentId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return Task.FromResult(false);
        }

        var restricted = WorkspaceDocumentConstants.SensitiveConfidentialityLevel;
        return _unitOfWork.WorkspaceDocumentRepository.AnyAsync(
            d => d.WorkspaceId == workspaceId
                && d.ContentHash == contentHash
                && d.ConfidentialityLevel == restricted
                && (excludeDocumentId == null || d.Id != excludeDocumentId),
            ct);
    }

    /// <summary>
    /// The upload's bytes, once, checked against the format its extension claims. WT-666.
    /// </summary>
    /// <remarks>
    /// Buffered rather than streamed because three things need the same bytes — the signature
    /// check, the SHA-256, and the encrypted write — and <see cref="IFormFile.OpenReadStream"/>
    /// gives no guarantee that a second open would yield the same content. The request pipeline
    /// caps an upload at 10MB (<c>RequestSizeLimit</c> on the controller action), and the storage
    /// layer already materialises the whole payload to encrypt it, so this adds no new ceiling.
    /// </remarks>
    private async Task<Result<byte[]>> ReadAndValidateContentAsync(IFormFile file, string extension, CancellationToken ct)
    {
        byte[] content;
        await using (var source = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }

        if (content.Length == 0)
        {
            return Result.Failure<byte[]>("The uploaded file is empty.", ErrorCodes.ValidationError);
        }

        // THE FILE NAME IS NOT EVIDENCE. Until this check, the only question asked about an upload
        // was whether its name ended in an accepted extension — so renaming payload.exe to
        // payload.pdf was enough to have it encrypted, stored, handed to the text extractor and,
        // for the AI-readable extensions, chunked into the vector store.
        if (!DocumentContentHelper.MatchesExtensionSignature(content, extension))
        {
            return Result.Failure<byte[]>(
                $"This file's contents do not match its {extension} extension — expected {DocumentContentHelper.DescribeExpectedFormat(extension)}. Rename it to its real format and upload it again.",
                ErrorCodes.ValidationError);
        }

        return Result.Success(content);
    }

    /// <summary>
    /// Answers a content collision, or returns null when there is none and the upload should
    /// proceed. WT-666.
    /// </summary>
    /// <remarks>
    /// WHY THE EXISTING DOCUMENT IS NOT ALWAYS NAMED.
    ///
    /// The match is over every live document in the workspace, because that is what a duplicate
    /// IS — a second copy of the same bytes and a second set of AI chunks, whoever uploaded it.
    /// But the collision must not become a way to learn that a document one cannot open exists, so
    /// the DETAILS are gated through <see cref="IDocumentAccessEvaluator"/> with the same View
    /// permission every other read uses. No new visibility rule is invented here; a caller who
    /// fails that check is told the bytes are already present and offered `create_new`, and that
    /// is all.
    ///
    /// `replace` is deliberately NOT handled here. Replacing means writing over a document that
    /// already has an id, an approval state and a history, which is the re-upload path — and its
    /// authorization (uploader or Owner/Admin) belongs with it rather than being re-derived here.
    /// </remarks>
    private async Task<Result<UploadDocumentOutcomeDto>?> ResolveDuplicateAsync(
        Guid workspaceId,
        Guid userId,
        string contentHash,
        string duplicateStrategy,
        UploadDocumentApiRequest request,
        CancellationToken ct)
    {
        var existing = await _unitOfWork.WorkspaceDocumentRepository.FirstOrDefaultAsync(
            d => d.WorkspaceId == workspaceId && d.ContentHash == contentHash && d.DeletedAt == null, "", ct);

        if (existing is null)
        {
            return null;
        }

        var canView = await _accessEvaluator.EvaluateAccessAsync(
            userId, workspaceId, existing.Id, WorkspaceDocumentPermissions.View, ct);

        if (string.Equals(duplicateStrategy, WorkspaceDocumentConstants.DuplicateStrategies.Replace, StringComparison.Ordinal))
        {
            var reuploaded = await ReuploadDocumentAsync(
                workspaceId,
                existing.Id,
                new ReuploadDocumentApiRequest(request.File, request.Name),
                userId,
                ct);

            return reuploaded.IsSuccess && reuploaded.Value is not null
                ? Result.Success(new UploadDocumentOutcomeDto(UploadDocumentOutcomeDto.Replaced, reuploaded.Value))
                : Result.Failure<UploadDocumentOutcomeDto>(reuploaded.Error ?? "Failed to replace the existing document.", reuploaded.ErrorCode);
        }

        if (string.Equals(duplicateStrategy, WorkspaceDocumentConstants.DuplicateStrategies.Skip, StringComparison.Ordinal) && canView.IsSuccess)
        {
            var url = _urlProvider.GetDocumentDownloadUrl(workspaceId, existing.Id);
            var approver = await _unitOfWork.WorkspaceDocumentAuditRepository.FirstOrDefaultAsync(
                a => a.DocumentId == existing.Id && a.Action == WorkspaceDocumentConstants.AuditActions.ApproveDocument, "", ct);
            return Result.Success(new UploadDocumentOutcomeDto(
                UploadDocumentOutcomeDto.Skipped,
                existing.ToDto(url, approver?.ActorId)));
        }

        // Reject — and `skip` for a caller who cannot see what they would be keeping, which is the
        // same answer: nothing was written, come back and say what you want done.
        //
        // A SUCCESSFUL Result carrying the `duplicate` outcome, not a Failure. Result has no
        // payload on the failure side, so a Failure here could say "already present" without being
        // able to say WHICH document — and the three choices the ticket asks for are unanswerable
        // without that. The controller turns this outcome into the 409.
        return Result.Success(new UploadDocumentOutcomeDto(
            UploadDocumentOutcomeDto.Duplicate,
            null,
            canView.IsSuccess
                ? new DocumentDuplicateDto(existing.Id, existing.Name, existing.FileName, existing.Status, existing.SizeBytes, existing.CreatedAt)
                : null));
    }

    public async Task<Result<PagedResult<WorkspaceDocumentDto>>> ListDocumentsAsync(Guid workspaceId, GetDocumentsQuery query, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<PagedResult<WorkspaceDocumentDto>>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure<PagedResult<WorkspaceDocumentDto>>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);

            var allPolicies = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.FindAsync(
                p => p.WorkspaceId == workspaceId, "", ct);
            var policiesByDoc = allPolicies.ToLookup(p => p.DocumentId);

            var documents = await _unitOfWork.WorkspaceDocumentRepository.FindAsync(
                d => d.WorkspaceId == workspaceId && d.DeletedAt == null, "", ct);
            var approvedByDocument = await _unitOfWork.WorkspaceDocumentAuditRepository.GetLatestApproverUserIdsByWorkspaceAsync(workspaceId, ct);

            var filteredDocs = documents.OrderByDescending(d => d.CreatedAt).AsEnumerable();
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                // SearchTextHelper, not string.Contains: a raw OrdinalIgnoreCase match reads the
                // punctuation of a file name as if it were part of the word. Nobody types
                // "bug-tracking" for a file called BUG-TRACKING-WT478-494, and nobody types the
                // diacritics on a Vietnamese title either — both were misses, and WarpBot
                // reported them to the user as "there is no such document".
                var search = query.Search;
                filteredDocs = filteredDocs.Where(d =>
                    SearchTextHelper.Matches(d.Name, search) ||
                    SearchTextHelper.Matches(d.FileName, search));
            }

            var (roomCache, participantsCache) = await BuildMeetingCachesAsync(member, filteredDocs, ct);

            var allowedDtos = new List<WorkspaceDocumentDto>();
            foreach (var doc in filteredDocs)
            {
                var docPolicies = policiesByDoc[doc.Id];
                var accessResult = await _accessEvaluator.EvaluateAccessAsync(
                    userId,
                    workspaceId,
                    doc,
                    WorkspaceDocumentPermissions.View,
                    member,
                    roleName,
                    docPolicies,
                    roomCache,
                    participantsCache,
                    ct);

                // Which version this caller gets. For anything that is not restricted this is
                // the evaluator's answer and nothing more; for a restricted document a member is
                // listed only when there is a masked copy for them to open.
                var version = await DecideListedVersionAsync(
                    userId, workspaceId, doc, member, roleName, docPolicies, roomCache, participantsCache, accessResult, ct);

                if (version != DocumentContentVersion.None)
                {
                    var isOriginal = version == DocumentContentVersion.Original;
                    approvedByDocument.TryGetValue(doc.Id, out var approvedBy);
                    allowedDtos.Add(doc.ToDto(
                            isOriginal ? _urlProvider.GetDocumentDownloadUrl(workspaceId, doc.Id) : null,
                            approvedBy)
                        with
                        {
                            ContentAccess = version.ToWireValue(),
                            MaskedVersionAvailable = !isOriginal
                        });
                }
            }

            var totalCount = allowedDtos.Count;
            var paginatedItems = allowedDtos
                .Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToList();

            var pagedResult = new PagedResult<WorkspaceDocumentDto>(paginatedItems, query.Page, query.PageSize, totalCount);
            return Result.Success(pagedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing documents. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<WorkspaceDocumentDto>>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceDocumentDto>> GetDocumentByIdAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.View, ct);

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);

            // A refusal stands as it always has — unless the document is restricted, in which
            // case the caller may still be owed its MASKED copy and the decision below settles it.
            var mayHaveMaskedVersion = document != null
                && document.WorkspaceId == workspaceId
                && document.DeletedAt == null
                && document.IsRestricted();
            if (!accessResult.IsSuccess && !mayHaveMaskedVersion)
            {
                return Result.Failure<WorkspaceDocumentDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            if (document == null)
            {
                return Result.Failure<WorkspaceDocumentDto>("Document not found.", ErrorCodes.NotFound);
            }

            var content = await ResolveContentAccessAsync(userId, workspaceId, document, WorkspaceDocumentPermissions.View, accessResult, ct);
            if (!DocumentContentAccessDecision.CanOpenDocument(content))
            {
                return Result.Failure<WorkspaceDocumentDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.GetDocumentDetails, logger: _logger, ct: ct);

            var approvalAudit = await _unitOfWork.WorkspaceDocumentAuditRepository.FirstOrDefaultAsync(
                a => a.DocumentId == documentId &&
                     a.Action == WorkspaceDocumentConstants.AuditActions.ApproveDocument,
                "",
                ct);

            // WT-633: the reviewer's reason, on the detail route only. The list evaluates access
            // for every document it returns and this is a detail-page fact; asking for it there
            // would add a second audit query per row.
            var rejectionReason = await GetLatestRejectionReasonAsync(documentId, ct);

            return Result.Success(await ToDetailDtoAsync(document, content, approvalAudit?.ActorId, rejectionReason, ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while getting document details. DocumentId: {DocumentId}", documentId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceDocumentDto>> PatchDocumentMetadataAsync(Guid workspaceId, Guid documentId, PatchDocumentRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
            if (!canManage)
            {
                return Result.Failure<WorkspaceDocumentDto>("Forbidden. Only owner or admin can edit document metadata.", ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.DeletedAt != null)
            {
                return Result.Failure<WorkspaceDocumentDto>("Document not found.", ErrorCodes.NotFound);
            }

            if (request.Name != null)
            {
                document.Name = request.Name;
            }

            if (!string.IsNullOrWhiteSpace(request.ConfidentialityLevel))
            {
                // Same gate as upload, and it has to be here too: this is the path that RE-labels
                // a document, so it is the one that can quietly turn a restricted document into an
                // unrecognised label that every policy check reads as not-restricted.
                var level = WorkspaceDocumentHelper.NormalizeConfidentialityLevel(request.ConfidentialityLevel);
                if (level is null)
                {
                    return Result.Failure<WorkspaceDocumentDto>(
                        $"Unsupported confidentiality level. Allowed values are: {WorkspaceDocumentHelper.SupportedConfidentialityLevels}.",
                        ErrorCodes.ValidationError);
                }

                var wasRestricted = document.IsRestricted();
                document.ConfidentialityLevel = level;
                var isNowRestricted = document.IsRestricted();

                // RE-LABELLING HAS TO MOVE THE VECTORS TOO.
                //
                // DocumentSecurityGuardrailHelper.HasBasicIndexEligibility refuses to index a
                // restricted document — but it is only ever consulted at UPLOAD. So a document
                // uploaded as public_internal was embedded into Qdrant, and marking it restricted
                // afterwards changed the label, the list and the access checks while leaving the
                // chunks exactly where they were: the assistant went on answering out of a
                // document the workspace had just declared confidential.
                //
                // Mirrors the IsAiAllowed branch below, which has always done this. The two
                // switches gate the same thing — whether the model may read this document — and
                // only one of them was wired to the index.
                if (!wasRestricted && isNowRestricted)
                {
                    document.AiEligible = false;
                    await _eventPublisher.PublishDocumentDeletedAsync(documentId, workspaceId, ct);
                }
                else if (wasRestricted && !isNowRestricted && document.IsIndexEligible())
                {
                    // Re-indexed only when the rest of the gate already passes. Publishing for a
                    // pending_approval document would index something nobody has approved yet.
                    // IsIndexEligible rather than a local copy of its conditions: this branch was
                    // spelling out two of the four, and the retention state it left out means a
                    // document staged for deletion could be re-indexed by un-restricting it.
                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString();
                    await _eventPublisher.PublishDocumentUploadedAsync(
                        document.Id,
                        workspaceId,
                        document.StorageKey,
                        document.FileName,
                        document.FileExtension,
                        userId,
                        document.ConfidentialityLevel,
                        ct);
                }
            }

            if (request.IsAiAllowed.HasValue && request.IsAiAllowed.Value != document.IsAiAllowed)
            {
                var wasAllowed = document.IsAiAllowed;
                document.IsAiAllowed = request.IsAiAllowed.Value;

                if (!document.IsAiAllowed)
                {
                    // Toggled to Administrative Document (IsAiAllowed = false)
                    document.AiEligible = false;
                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();

                    // Invalidate and delete existing vectors in Qdrant Vector DB
                    await _eventPublisher.PublishDocumentDeletedAsync(documentId, workspaceId, ct);
                }
                else
                {
                    // Toggled back to AI Context Document (IsAiAllowed = true)
                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString();
                    if (string.Equals(document.Status, WorkspaceDocumentStatus.@public.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        await _eventPublisher.PublishDocumentUploadedAsync(
                            document.Id,
                            workspaceId,
                            document.StorageKey,
                            document.FileName,
                            document.FileExtension,
                            userId,
                            document.ConfidentialityLevel,
                            ct);
                    }
                }
            }

            document.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);
            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Updated,
                document.UpdatedAt,
                userId,
                ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.PatchDocumentMetadata, request, _logger, ct);

            var downloadUrl = _urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id);
            return Result.Success(document.ToDto(downloadUrl));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while patching document metadata. DocumentId: {DocumentId}", documentId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> AddAccessPolicyAsync(Guid workspaceId, Guid documentId, AddAccessPolicyRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
            if (!canManage)
            {
                return Result.Failure("Forbidden. Only workspace Owner/Admin or the document owner can manage document access policies.", ErrorCodes.Forbidden);
            }

            var normalizedSubjectType = WorkspaceDocumentHelper.NormalizePolicySubjectType(request.SubjectType);
            var normalizedPermission = request.Permission?.Trim().ToLowerInvariant();
            var normalizedEffect = request.Effect?.Trim().ToUpperInvariant();
            var normalizedSubjectKey = request.SubjectKey?.Trim();

            if (normalizedSubjectType == null)
            {
                return Result.Failure("SubjectType must be User, Role, or MembershipType.", ErrorCodes.ValidationError);
            }
            if (!WorkspaceDocumentHelper.IsSupportedPolicyPermission(normalizedPermission))
            {
                return Result.Failure("Permission must be view, download, or ai_retrieval.", ErrorCodes.ValidationError);
            }
            if (normalizedEffect is not WorkspacePolicyConstants.EffectAllow and not WorkspacePolicyConstants.EffectDeny)
            {
                return Result.Failure("Effect must be ALLOW or DENY.", ErrorCodes.ValidationError);
            }

            if (normalizedSubjectType == WorkspacePolicyConstants.SubjectTypeUser && !request.SubjectId.HasValue)
            {
                return Result.Failure("SubjectId is required for a User policy.", ErrorCodes.ValidationError);
            }
            // MEMBER ONLY, and refusing the other two is the point rather than an oversight.
            //
            // DocumentAccessEvaluator matches a Role policy against the caller's role name
            // whoever they are, so a DENY on Owner was evaluated and locked every owner in the
            // workspace out of the document. The web has only ever offered Member for exactly
            // that reason — but the API accepted all three, so the foot-gun was one curl away
            // from a control the UI deliberately does not draw.
            //
            // Nothing is lost by refusing them. An ALLOW on Owner or Admin is a no-op: they
            // already reach every document in the workspace. A DENY is worse than a no-op — it
            // is not even a boundary, since CanManagePoliciesAsync answers from role and never
            // reads policies, so the admin it names can simply delete it.
            //
            // Existing rows, if any, keep evaluating: this is a write-side gate, and any owner
            // can remove one. Nothing silently changes meaning underneath a workspace.
            if (normalizedSubjectType == WorkspacePolicyConstants.SubjectTypeRole
                && (normalizedSubjectKey == null || !normalizedSubjectKey.IsMember()))
            {
                return Result.Failure(
                    "Role policy SubjectKey must be Member. Owners and admins already reach every document in the workspace, and a rule naming them would only lock them out of one.",
                    ErrorCodes.ValidationError);
            }
            if (normalizedSubjectType == WorkspacePolicyConstants.SubjectTypeMembershipType
                && !Enum.TryParse<MembershipType>(normalizedSubjectKey, true, out _))
            {
                return Result.Failure("MembershipType policy SubjectKey must be Internal or External.", ErrorCodes.ValidationError);
            }

            normalizedSubjectKey = normalizedSubjectType switch
            {
                WorkspacePolicyConstants.SubjectTypeRole => normalizedSubjectKey!.ToWorkspaceMemberRole().ToRoleName(),
                WorkspacePolicyConstants.SubjectTypeMembershipType => Enum.Parse<MembershipType>(normalizedSubjectKey!, true).ToString(),
                _ => null
            };
            var normalizedSubjectId = normalizedSubjectType == WorkspacePolicyConstants.SubjectTypeUser
                ? request.SubjectId
                : null;

            var existingPolicy = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.FirstOrDefaultAsync(
                policy => policy.DocumentId == documentId
                          && policy.WorkspaceId == workspaceId
                          && policy.SubjectType == normalizedSubjectType
                          && policy.SubjectId == normalizedSubjectId
                          && policy.SubjectKey == normalizedSubjectKey
                          && policy.Permission == normalizedPermission,
                "",
                ct);
            if (existingPolicy != null)
            {
                return Result.Failure(
                    "A policy already exists for this subject and permission. Remove it before changing the effect.",
                    ErrorCodes.Conflict);
            }

            var normalizedRequest = request with
            {
                SubjectType = normalizedSubjectType,
                SubjectId = normalizedSubjectId,
                SubjectKey = normalizedSubjectKey,
                Permission = normalizedPermission,
                Effect = normalizedEffect
            };
            var policy = normalizedRequest.ToEntity(documentId, workspaceId, userId);

            await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.AddAsync(policy, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.AddAccessPolicy, new { policy.Id, policy.SubjectType, policy.SubjectKey, policy.Permission, policy.Effect }, _logger, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while adding document access policy. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RemoveAccessPolicyAsync(Guid workspaceId, Guid documentId, Guid policyId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
            if (!canManage)
            {
                return Result.Failure("Forbidden. Only workspace Owner/Admin or the document owner can manage document access policies.", ErrorCodes.Forbidden);
            }

            var policy = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.GetByIdAsync(policyId, ct);
            if (policy == null || policy.DocumentId != documentId || policy.WorkspaceId != workspaceId)
            {
                return Result.Failure("Policy not found or does not belong to this document.", ErrorCodes.NotFound);
            }

            _unitOfWork.WorkspaceDocumentAccessPolicyRepository.Remove(policy);
            await _unitOfWork.SaveChangesAsync(ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.RemoveAccessPolicy, new { policy.Id }, _logger, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while removing document access policy. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PagedResult<WorkspaceDocumentAccessPolicyDto>>> GetAccessPoliciesAsync(Guid workspaceId, Guid documentId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<PagedResult<WorkspaceDocumentAccessPolicyDto>>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
            if (!canManage)
            {
                return Result.Failure<PagedResult<WorkspaceDocumentAccessPolicyDto>>("Forbidden. Only workspace Owner/Admin or the document owner can view access policies.", ErrorCodes.Forbidden);
            }

            var (policies, totalCount) = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository
                .GetPagedAccessPoliciesAsync(documentId, query.Page, query.PageSize, isDescending: true, ct);

            var dtos = policies.Select(p => p.ToDto()).ToList();
            var pagedResult = new PagedResult<WorkspaceDocumentAccessPolicyDto>(dtos, query.Page, query.PageSize, totalCount);

            return Result.Success(pagedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching document access policies. DocumentId: {DocumentId}", documentId);
            return Result.Failure<PagedResult<WorkspaceDocumentAccessPolicyDto>>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ApproveDocumentAsync(Guid workspaceId, Guid documentId, ApproveDocumentRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            if (!roleName.IsOwnerOrAdmin())
            {
                // Was WorkspaceConstants.Errors.OnlyOwnerAdminCanInvite — a Member blocked from
                // approving a document was told "Only Owner or Admin can invite members.", the
                // wrong action entirely, copy-pasted from the invitation flow. The 403 itself was
                // correct; only the message named the wrong operation.
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanApproveDocuments, ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            var isPendingApproval = string.Equals(document.Status, WorkspaceDocumentStatus.pending_approval.ToString(), StringComparison.OrdinalIgnoreCase);
            var isRevisionReview = !isPendingApproval
                && document.IsPublic()
                && WorkspaceDocumentHelper.HasPendingRevision(document);
            if (!isPendingApproval && !isRevisionReview)
            {
                return Result.Failure("Document is not pending approval.", ErrorCodes.ValidationError);
            }

            // A REJECTION WITHOUT A REASON IS THE BUG. WT-633's reporter described an uploader who
            // could see that their document was refused and had no way to learn why, and the cause
            // was here: nothing ever asked the reviewer for a sentence. Required on the reject
            // branch only — an approval needs no justification.
            var reason = (request.Reason ?? string.Empty).Trim();
            if (!request.Approve && reason.Length == 0)
            {
                return Result.Failure("A reason is required when rejecting a document.", ErrorCodes.ValidationError);
            }

            if (reason.Length > WorkspaceDocumentConstants.MaxRejectionReasonLength)
            {
                return Result.Failure(
                    $"The reason must be {WorkspaceDocumentConstants.MaxRejectionReasonLength} characters or fewer. This one is {reason.Length}.",
                    ErrorCodes.ValidationError);
            }

            if (isRevisionReview)
            {
                return request.Approve
                    ? await PromotePendingRevisionAsync(document, reason, userId, ct)
                    : await DiscardPendingRevisionAsync(document, reason, userId, ct);
            }

            if (request.Approve)
            {
                document.Status = WorkspaceDocumentStatus.@public.ToString();
                document.AiEligible = false;
                document.IngestionStatus = document.IsAiAllowed
                    ? WorkspaceDocumentIngestionStatus.pending.ToString()
                    : WorkspaceDocumentIngestionStatus.skipped.ToString();
                document.UpdatedAt = DateTime.UtcNow;

                _unitOfWork.WorkspaceDocumentRepository.Update(document);
                if (document.IsAiAllowed)
                {
                    await _eventPublisher.PublishDocumentUploadedAsync(
                        document.Id,
                        workspaceId,
                        document.StorageKey,
                        document.FileName,
                        document.FileExtension,
                        document.UploadedBy ?? userId,
                        document.ConfidentialityLevel,
                        ct);
                }
                await _unitOfWork.SaveChangesAsync(ct);
                await _eventPublisher.PublishDocumentLifecycleAsync(
                    document.Id,
                    workspaceId,
                    document.Status,
                    document.IngestionStatus,
                    WorkspaceDocumentConstants.LifecycleEvents.Approved,
                    document.UpdatedAt,
                    userId,
                    ct);

                await _unitOfWork.AuditAsync(
                    document.Id,
                    workspaceId,
                    userId,
                    WorkspaceDocumentConstants.AuditActions.ApproveDocument,
                    reason.Length > 0 ? new { reason } : null,
                    _logger,
                    ct);
            }
            else
            {
                document.Status = WorkspaceDocumentStatus.rejected.ToString();
                document.AiEligible = false;
                document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
                document.UpdatedAt = DateTime.UtcNow;

                _unitOfWork.WorkspaceDocumentRepository.Update(document);
                await _unitOfWork.SaveChangesAsync(ct);
                await _eventPublisher.PublishDocumentLifecycleAsync(
                    document.Id,
                    workspaceId,
                    document.Status,
                    document.IngestionStatus,
                    WorkspaceDocumentConstants.LifecycleEvents.Rejected,
                    document.UpdatedAt,
                    userId,
                    ct);

                // The reviewer's words, on the row. This audit call used to pass no metadata at
                // all, which is why WT-633 could not be answered by reading anything: the decision
                // was recorded and the reason for it was discarded at the moment it was made.
                await _unitOfWork.AuditAsync(
                    document.Id,
                    workspaceId,
                    userId,
                    WorkspaceDocumentConstants.AuditActions.RejectDocument,
                    new { reason },
                    _logger,
                    ct);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while approving/rejecting document. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// WT-854 — approve a corrected version of a published document: it becomes the document.
    ///
    /// The pending values replace the live ones, and the document is re-ingested through the same
    /// pipeline an approval always uses (the security scan reads StorageKey, so it now reads the
    /// new file). The superseded blob is kept, as every revision's is, and named in the audit row.
    /// Its chunks are purged synchronously after the commit — the DocumentDeleted invalidation has
    /// no consumer (WT-871), so publishing it would leave the old text answering in WarpBot.
    /// </summary>
    private async Task<Result> PromotePendingRevisionAsync(
        WorkspaceDocument document, string reason, Guid userId, CancellationToken ct)
    {
        var workspaceId = document.WorkspaceId;
        var previousStorageKey = document.StorageKey;
        var previousFileName = document.FileName;
        var extension = document.PendingFileExtension ?? document.FileExtension;

        document.StorageKey = document.PendingStorageKey!;
        document.StorageProvider = document.PendingStorageProvider ?? document.StorageProvider;
        document.Name = document.PendingName ?? document.Name;
        document.FileName = document.PendingFileName ?? document.FileName;
        document.FileExtension = extension;
        document.MimeType = document.PendingMimeType ?? WorkspaceDocumentHelper.GetSafeContentType(extension);
        document.DocumentType = extension.TrimStart('.').ToUpperInvariant();
        document.SizeBytes = document.PendingSizeBytes ?? document.SizeBytes;
        document.ContentHash = document.PendingContentHash;
        WorkspaceDocumentHelper.ClearPendingRevision(document);

        // The same rule upload applies: an image cannot be AI-readable however the switch was left.
        document.IsAiAllowed = document.IsAiAllowed && WorkspaceDocumentHelper.IsAiReadableExtension(extension);
        document.AiEligible = false;
        document.LastIndexedAt = null;
        document.IngestionFailureReason = null;
        document.IngestionStatus = document.IsAiAllowed
            ? WorkspaceDocumentIngestionStatus.pending.ToString()
            : WorkspaceDocumentIngestionStatus.skipped.ToString();
        document.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.WorkspaceDocumentRepository.Update(document);
        if (document.IsAiAllowed)
        {
            await _eventPublisher.PublishDocumentUploadedAsync(
                document.Id,
                workspaceId,
                document.StorageKey,
                document.FileName,
                document.FileExtension,
                document.UploadedBy ?? userId,
                document.ConfidentialityLevel,
                ct);
        }
        await _unitOfWork.SaveChangesAsync(ct);

        // After the commit, so a failed save cannot strip the index of the approved file it still
        // serves. Re-ingestion of the new file is queued in the same commit and runs through the
        // outbox and the security scan, which is far slower than this call.
        var vectorsPurged = await TryPurgeDocumentChunksAsync(workspaceId, document.Id, ct);

        await _eventPublisher.PublishDocumentLifecycleAsync(
            document.Id,
            workspaceId,
            document.Status,
            document.IngestionStatus,
            WorkspaceDocumentConstants.LifecycleEvents.Approved,
            document.UpdatedAt,
            userId,
            ct);

        await _unitOfWork.AuditAsync(
            document.Id,
            workspaceId,
            userId,
            WorkspaceDocumentConstants.AuditActions.ApproveDocument,
            new
            {
                revision = true,
                previousStorageKey,
                previousFileName,
                fileName = document.FileName,
                vectorsPurged,
                reason = reason.Length > 0 ? reason : null
            },
            _logger,
            ct);

        return Result.Success();
    }

    /// <summary>
    /// WT-854 — reject a corrected version of a published document. The document never stopped
    /// being the approved file, so there is nothing to restore: the pending slot is emptied and
    /// its object deleted. The reason goes on the audit row, like any rejection.
    /// </summary>
    private async Task<Result> DiscardPendingRevisionAsync(
        WorkspaceDocument document, string reason, Guid userId, CancellationToken ct)
    {
        var workspaceId = document.WorkspaceId;
        var pendingFile = WorkspaceDocumentHelper.PendingRevisionFile(document);

        WorkspaceDocumentHelper.ClearPendingRevision(document);
        document.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.WorkspaceDocumentRepository.Update(document);
        await _unitOfWork.SaveChangesAsync(ct);

        // After the commit: deleting first would leave a row pointing at nothing if the save
        // failed. A failed delete leaves an orphaned encrypted blob nothing references, which is
        // the harmless direction — and it is logged and recorded.
        var pendingFileDeleted = true;
        try
        {
            await _storage.DeleteDocumentContentAsync(pendingFile, ct);
        }
        catch (Exception ex)
        {
            pendingFileDeleted = false;
            _logger.LogError(
                ex,
                "Could not delete the rejected revision {StorageKey} of document {DocumentId}.",
                pendingFile.StorageKey,
                document.Id);
        }

        await _eventPublisher.PublishDocumentLifecycleAsync(
            document.Id,
            workspaceId,
            document.Status,
            document.IngestionStatus,
            WorkspaceDocumentConstants.LifecycleEvents.Rejected,
            document.UpdatedAt,
            userId,
            ct);

        await _unitOfWork.AuditAsync(
            document.Id,
            workspaceId,
            userId,
            WorkspaceDocumentConstants.AuditActions.RejectDocument,
            new
            {
                reason,
                revision = true,
                rejectedFileName = pendingFile.FileName,
                rejectedStorageKey = pendingFile.StorageKey,
                pendingFileDeleted
            },
            _logger,
            ct);

        return Result.Success();
    }

    /// <summary>
    /// Audit actions the history route leaves out. A read is not a decision, and
    /// GetDocumentDetails is written on every single view of the detail page.
    /// </summary>
    private static readonly string[] HistoryExcludedActions =
    [
        WorkspaceDocumentConstants.AuditActions.GetDocumentDetails,
        WorkspaceDocumentConstants.AuditActions.DownloadDocument
    ];

    /// <summary>The most recent rejection reason on record for a document, or null.</summary>
    private async Task<string?> GetLatestRejectionReasonAsync(Guid documentId, CancellationToken ct)
    {
        var audit = await _unitOfWork.WorkspaceDocumentAuditRepository.GetLatestActionAsync(
            documentId, WorkspaceDocumentConstants.AuditActions.RejectDocument, ct);
        return WorkspaceDocumentMapper.ReadAuditReason(audit?.Metadata);
    }

    /// <inheritdoc />
    public async Task<Result<WorkspaceDocumentDto>> ReuploadDocumentAsync(
        Guid workspaceId,
        Guid documentId,
        ReuploadDocumentApiRequest request,
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            if (await UploadTooLargeAsync(workspaceId, request.File.Length, ct) is { } tooLarge)
            {
                return Result.Failure<WorkspaceDocumentDto>(tooLarge, ErrorCodes.ValidationError);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            // THE UPLOADER, or an Owner/Admin. Not the View ACL: being allowed to read a document
            // is not being allowed to replace its contents, and a workspace-visible document is
            // readable by every Internal member. This is the same shape as the delete and archive
            // paths, which is the company this operation keeps.
            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isUploader = document.UploadedBy == userId || document.OwnerId == userId;
            if (!isUploader && !roleName.IsOwnerOrAdmin())
            {
                return Result.Failure<WorkspaceDocumentDto>(
                    "Forbidden. Only the uploader or a workspace owner or admin can replace this document's file.",
                    ErrorCodes.Forbidden);
            }

            // Rejected and published are the two states a replacement makes sense from. Refusing a
            // document already awaiting review keeps the reviewer from reading one file and
            // deciding about another, and refusing an archived or deleted one keeps a retired
            // document from being quietly brought back with new contents.
            var isRejected = string.Equals(document.Status, WorkspaceDocumentStatus.rejected.ToString(), StringComparison.OrdinalIgnoreCase);
            var isPublished = string.Equals(document.Status, WorkspaceDocumentStatus.@public.ToString(), StringComparison.OrdinalIgnoreCase);
            if (!isRejected && !isPublished)
            {
                return Result.Failure<WorkspaceDocumentDto>(
                    $"A document with status \"{document.Status}\" cannot be replaced.",
                    ErrorCodes.ValidationError);
            }

            // WT-854 — one revision under review at a time, for the same reason a document already
            // pending approval is refused: the reviewer must decide about the file they read.
            if (WorkspaceDocumentHelper.HasPendingRevision(document))
            {
                return Result.Failure<WorkspaceDocumentDto>(
                    "A corrected version of this document is already awaiting review. It must be approved or rejected before another one is uploaded.",
                    ErrorCodes.Conflict);
            }

            var name = string.IsNullOrWhiteSpace(request.Name) ? document.Name : request.Name.Trim();
            if (name.Length == 0)
            {
                return Result.Failure<WorkspaceDocumentDto>("Document name is required.", ErrorCodes.ValidationError);
            }

            if (name.Length > WorkspaceDocumentConstants.MaxDocumentNameLength)
            {
                return Result.Failure<WorkspaceDocumentDto>(
                    $"Document name must be {WorkspaceDocumentConstants.MaxDocumentNameLength} characters or fewer. This one is {name.Length}.",
                    ErrorCodes.ValidationError);
            }

            var note = (request.Note ?? string.Empty).Trim();
            if (note.Length > WorkspaceDocumentConstants.MaxRejectionReasonLength)
            {
                return Result.Failure<WorkspaceDocumentDto>(
                    $"The note must be {WorkspaceDocumentConstants.MaxRejectionReasonLength} characters or fewer. This one is {note.Length}.",
                    ErrorCodes.ValidationError);
            }

            var extension = WorkspaceDocumentHelper.NormalizeExtension(System.IO.Path.GetExtension(request.File.FileName));
            if (!WorkspaceDocumentHelper.IsSupportedUploadExtension(extension))
            {
                var allowed = string.Join(", ", WorkspaceDocumentConstants.SupportedUploadExtensions);
                return Result.Failure<WorkspaceDocumentDto>($"Unsupported file type. Allowed file types are: {allowed}.", ErrorCodes.ValidationError);
            }

            var contentResult = await ReadAndValidateContentAsync(request.File, extension, ct);
            if (!contentResult.IsSuccess || contentResult.Value is null)
            {
                return Result.Failure<WorkspaceDocumentDto>(contentResult.Error ?? "Invalid file.", contentResult.ErrorCode);
            }

            var content = contentResult.Value;

            // WT-854 — a PUBLISHED document keeps serving its approved file. The correction waits in
            // the pending slot, and readers, downloads and the AI index are untouched until a
            // reviewer approves it. Only a rejected document — which nobody is reading — is still
            // replaced in place below.
            if (isPublished)
            {
                return await StagePendingRevisionAsync(document, request, name, note, extension, content, userId, ct);
            }

            var now = DateTime.UtcNow;
            var previousStorageKey = document.StorageKey;
            var previousFileName = document.FileName;
            var rejectionReason = await GetLatestRejectionReasonAsync(documentId, ct);

            // Asked BEFORE the extension is overwritten. Whether vectors exist to purge is a fact
            // about the document as it stands, and reading it from the replacement's extension
            // would leave the old chunks in place whenever a .pdf was replaced by a .png.
            var hadVectors = document.IsAiAllowed
                && WorkspaceDocumentHelper.IsAiReadableExtension(document.FileExtension);

            // A NEW KEY, and the old blob is left exactly where it is. Reusing the key would
            // encrypt the replacement over the file the reviewer read, which would make
            // `previousStorageKey` in the audit row a pointer to bytes that no longer exist —
            // the audit trail would claim a completeness it did not have.
            document.StorageKey = WorkspaceDocumentHelper.GenerateRevisionStorageKey(workspaceId, documentId, extension, now);
            document.StorageProvider = _storage.StorageProviderName;
            document.Name = name;
            document.FileName = request.File.FileName;
            document.FileExtension = extension;
            document.MimeType = WorkspaceDocumentHelper.GetSafeContentType(extension);
            document.DocumentType = extension.TrimStart('.').ToUpperInvariant();
            document.SizeBytes = request.File.Length;
            document.ContentHash = DocumentContentHelper.ComputeSha256(content);
            document.UpdatedAt = now;

            // Same rule as a fresh upload: new bytes that were already found restricted elsewhere
            // in this workspace arrive restricted. (A label already on this row is never cleared.)
            if (await HasRestrictedTwinAsync(workspaceId, document.ContentHash, documentId, ct))
            {
                document.ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel;
            }

            // Back to the queue, whichever state it came from. A replaced file has not been read by
            // anyone, so it cannot keep a published document's approval — that is the difference
            // between this and editing a title.
            document.Status = WorkspaceDocumentStatus.pending_approval.ToString();
            document.AiEligible = false;

            // The same rule upload applies: an image cannot be AI-readable however the switch was
            // left, so replacing a PDF with a PNG turns indexing off rather than queueing work the
            // extractor cannot do.
            document.IsAiAllowed = document.IsAiAllowed && WorkspaceDocumentHelper.IsAiReadableExtension(extension);
            document.IngestionStatus = document.IsAiAllowed
                ? WorkspaceDocumentIngestionStatus.awaiting_approval.ToString()
                : WorkspaceDocumentIngestionStatus.skipped.ToString();

            await _storage.SaveDocumentContentAsync(document, new MemoryStream(content, writable: false), ct);

            try
            {
                _unitOfWork.WorkspaceDocumentRepository.Update(document);

                // THE OLD CHUNKS HAVE TO GO. The document id is unchanged, so every vector point
                // keyed to it still describes the superseded file — and the document is no longer
                // published, so nothing may answer from it either way. Re-indexing happens on
                // approval, through the path that already does it.
                if (hadVectors)
                {
                    await _eventPublisher.PublishDocumentDeletedAsync(documentId, workspaceId, ct);
                }

                await _unitOfWork.SaveChangesAsync(ct);
            }
            catch
            {
                // The replacement blob is written but the row still points at the old key — remove
                // the orphan rather than leaving an encrypted file nothing references. The previous
                // file is untouched by this, which is the point of the new key.
                await _storage.DeleteDocumentContentAsync(document, ct);
                throw;
            }

            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.PendingApproval,
                document.UpdatedAt,
                userId,
                ct);

            // The whole point of the ticket, in one row: what was replaced, where the old bytes
            // still are, and the feedback this revision is answering.
            await _unitOfWork.AuditAsync(
                document.Id,
                workspaceId,
                userId,
                WorkspaceDocumentConstants.AuditActions.ReuploadDocument,
                new
                {
                    previousStorageKey,
                    previousFileName,
                    fileName = document.FileName,
                    rejectionReason,
                    reason = note.Length > 0 ? note : null
                },
                _logger,
                ct);

            var downloadUrl = _urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id);
            return Result.Success(document.ToDto(downloadUrl, null, rejectionReason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while re-uploading document. DocumentId: {DocumentId}", documentId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// WT-854 — store a corrected file for a PUBLISHED document beside the approved one.
    ///
    /// Before this, a re-upload overwrote StorageKey at once and sent the document back to
    /// pending_approval: readers lost the approved document the moment somebody uploaded a fix, the
    /// unreviewed file was what downloads served, and Reject had nothing to restore — the approved
    /// file's key survived only in the audit row. Now nothing a reader can reach changes: status,
    /// StorageKey, the file metadata, the extracted text and the AI index all keep describing the
    /// approved file. ApproveDocumentAsync promotes this slot; a rejection deletes it.
    /// </summary>
    private async Task<Result<WorkspaceDocumentDto>> StagePendingRevisionAsync(
        WorkspaceDocument document,
        ReuploadDocumentApiRequest request,
        string name,
        string note,
        string extension,
        byte[] content,
        Guid userId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var workspaceId = document.WorkspaceId;

        // A NEW KEY, as for any revision: the approved blob stays exactly where it is.
        document.PendingStorageKey = WorkspaceDocumentHelper.GenerateRevisionStorageKey(workspaceId, document.Id, extension, now);
        document.PendingStorageProvider = _storage.StorageProviderName;
        document.PendingName = string.Equals(name, document.Name, StringComparison.Ordinal) ? null : name;
        document.PendingFileName = request.File.FileName;
        document.PendingFileExtension = extension;
        document.PendingMimeType = WorkspaceDocumentHelper.GetSafeContentType(extension);
        document.PendingSizeBytes = request.File.Length;
        document.PendingContentHash = DocumentContentHelper.ComputeSha256(content);
        document.PendingNote = note.Length > 0 ? note : null;
        document.PendingUploadedBy = userId;
        document.PendingUploadedAt = now;

        var pendingFile = WorkspaceDocumentHelper.PendingRevisionFile(document);
        await _storage.SaveDocumentContentAsync(pendingFile, new MemoryStream(content, writable: false), ct);

        try
        {
            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch
        {
            // The row never learned about the pending object, so nothing references it.
            await _storage.DeleteDocumentContentAsync(pendingFile, ct);
            throw;
        }

        await _eventPublisher.PublishDocumentLifecycleAsync(
            document.Id,
            workspaceId,
            document.Status,
            document.IngestionStatus,
            WorkspaceDocumentConstants.LifecycleEvents.PendingApproval,
            document.UpdatedAt,
            userId,
            ct);

        await _unitOfWork.AuditAsync(
            document.Id,
            workspaceId,
            userId,
            WorkspaceDocumentConstants.AuditActions.ReuploadDocument,
            new
            {
                pendingRevision = true,
                pendingStorageKey = document.PendingStorageKey,
                currentStorageKey = document.StorageKey,
                previousFileName = document.FileName,
                fileName = document.PendingFileName,
                reason = document.PendingNote
            },
            _logger,
            ct);

        var downloadUrl = _urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id);
        return Result.Success(document.ToDto(downloadUrl));
    }

    /// <inheritdoc />
    public async Task<Result<DocumentDownloadStreamDto>> DownloadPendingRevisionAsync(
        Guid workspaceId,
        Guid documentId,
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null
                || !WorkspaceDocumentHelper.HasPendingRevision(document))
            {
                return Result.Failure<DocumentDownloadStreamDto>("No corrected version is awaiting review.", ErrorCodes.NotFound);
            }

            // The people who decide about it and the people who sent it — not every reader of the
            // published document. An unreviewed file is not published content.
            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isUploader = document.UploadedBy == userId || document.OwnerId == userId || document.PendingUploadedBy == userId;
            if (!isUploader && !roleName.IsOwnerOrAdmin())
            {
                return Result.Failure<DocumentDownloadStreamDto>(
                    "Only a reviewer or the uploader can open a version that is awaiting review.",
                    ErrorCodes.Forbidden);
            }

            var pendingFile = WorkspaceDocumentHelper.PendingRevisionFile(document);
            var stream = await _storage.GetDecryptedStreamAsync(pendingFile, ct);

            return Result.Success(new DocumentDownloadStreamDto(
                stream,
                WorkspaceDocumentHelper.GetSafeContentType(pendingFile.FileExtension),
                pendingFile.FileName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while downloading a pending revision. DocumentId: {DocumentId}", documentId);
            return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<DocumentHistoryEntryDto>>> GetDocumentHistoryAsync(
        Guid workspaceId,
        Guid documentId,
        GetWorkspacesQuery query,
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<PagedResult<DocumentHistoryEntryDto>>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            // The document's own View ACL decides this. Its history says who uploaded it, who
            // refused it and what they wrote — strictly more than the document row — so it can
            // never be readable by someone the document itself is not.
            var accessResult = await _accessEvaluator.EvaluateAccessAsync(
                userId, workspaceId, documentId, WorkspaceDocumentPermissions.View, ct);
            if (!accessResult.IsSuccess)
            {
                return Result.Failure<PagedResult<DocumentHistoryEntryDto>>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure<PagedResult<DocumentHistoryEntryDto>>(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            var (items, totalCount) = await _unitOfWork.WorkspaceDocumentAuditRepository.GetPagedAuditsAsync(
                documentId,
                query.Page,
                query.PageSize,
                isDescending: true,
                excludeActions: HistoryExcludedActions,
                ct);

            var entries = items.Select(a => a.ToHistoryDto()).ToList();

            return Result.Success(new PagedResult<DocumentHistoryEntryDto>(entries, query.Page, query.PageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while reading document history. DocumentId: {DocumentId}", documentId);
            return Result.Failure<PagedResult<DocumentHistoryEntryDto>>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<DocumentDownloadStreamDto>> DownloadDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.Download, ct);
            if (!accessResult.IsSuccess)
            {
                return Result.Failure<DocumentDownloadStreamDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null)
            {
                return Result.Failure<DocumentDownloadStreamDto>("Document not found.", ErrorCodes.NotFound);
            }

            // THE ORIGINAL BYTES. A restricted document's original goes to Owner/Admin and the
            // uploader; everybody else is on the masked copy or on nothing, whatever policy let
            // them through the evaluator above. Same function the detail route answers from.
            var content = await ResolveContentAccessAsync(userId, workspaceId, document, WorkspaceDocumentPermissions.Download, accessResult, ct);
            if (DocumentContentAccessDecision.Decide(content) != DocumentContentVersion.Original)
            {
                return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.AccessDeniedOriginalContent, ErrorCodes.Forbidden);
            }

            var stream = await _storage.GetDecryptedStreamAsync(document, ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.DownloadDocument, logger: _logger, ct: ct);

            return Result.Success(new DocumentDownloadStreamDto(stream, WorkspaceDocumentHelper.GetSafeContentType(document.FileExtension), document.FileName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while downloading document. DocumentId: {DocumentId}", documentId);
            return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <inheritdoc />
    public async Task<Result<DocumentDownloadStreamDto>> DownloadMaskedDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            // Download, not View: this route is also what the page's preview reads, exactly as
            // the original download is for an original — so a DENY on `download` closes both.
            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.Download, ct);

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure<DocumentDownloadStreamDto>(accessResult.Error ?? WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.Forbidden);
            }

            var content = await ResolveContentAccessAsync(userId, workspaceId, document, WorkspaceDocumentPermissions.Download, accessResult, ct);
            if (DocumentContentAccessDecision.Decide(content) == DocumentContentVersion.None)
            {
                return Result.Failure<DocumentDownloadStreamDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            if (!DocumentContentAccessDecision.CanReadMaskedVersion(content))
            {
                return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.MaskedVersionNotFound, ErrorCodes.NotFound);
            }

            var stream = await _storage.GetMaskedFileStreamAsync(document, ct);
            if (stream == null)
            {
                return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.MaskedVersionNotFound, ErrorCodes.NotFound);
            }

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.DownloadMaskedDocument, logger: _logger, ct: ct);

            return Result.Success(new DocumentDownloadStreamDto(
                stream,
                WorkspaceDocumentHelper.GetSafeContentType(document.FileExtension),
                WorkspaceDocumentHelper.MaskedDownloadFileName(document)));
        }
        catch (Exception ex)
        {
            // Including a masked copy that fails its integrity check. The answer is an error —
            // there is no path from here to the original.
            _logger.LogError(ex, "Error occurred while downloading the masked copy. DocumentId: {DocumentId}", documentId);
            return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <inheritdoc />
    public async Task<Result<WorkspaceDocumentDto>> RescanMaskedVersionAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var (loaded, isOwnerOrAdmin, _, failure) = await LoadForVisibilityChangeAsync(workspaceId, documentId, userId, ct);
            if (failure != null || loaded == null)
            {
                return failure ?? Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            var document = loaded;

            if (!isOwnerOrAdmin)
            {
                return Result.Failure<WorkspaceDocumentDto>("Forbidden. Only a workspace Owner or Admin can re-scan a document.", ErrorCodes.Forbidden);
            }

            if (!document.IsRestricted())
            {
                return Result.Failure<WorkspaceDocumentDto>("Only a restricted document has a masked version.", ErrorCodes.ValidationError);
            }

            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.View, ct);
            var state = await ReadMaskedVersionStateAsync(document, await SafeMaskedFileExistsAsync(document, ct), ct);

            // Already asked and not answered yet: one scan is enough.
            if (state.Status != WorkspaceDocumentMaskedVersionStatuses.Pending)
            {
                // The REQUEST is the audit row — the guardrail looks for it when the event below
                // arrives, because a restricted document is otherwise skipped. So unlike every
                // other audit write it is not allowed to fail quietly, and it is saved together
                // with the outbox row: either both exist or neither does.
                await _unitOfWork.WorkspaceDocumentAuditRepository.AddAsync(
                    WorkspaceDocumentMapper.ToAuditEntity(
                        documentId,
                        workspaceId,
                        userId,
                        WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanRequested),
                    ct);

                // The same event an upload publishes. No second pipeline.
                await _eventPublisher.PublishDocumentUploadedAsync(
                    document.Id,
                    workspaceId,
                    document.StorageKey,
                    document.FileName,
                    document.FileExtension,
                    userId,
                    document.ConfidentialityLevel,
                    ct);
                await _unitOfWork.SaveChangesAsync(ct);
            }

            var content = await ResolveContentAccessAsync(userId, workspaceId, document, WorkspaceDocumentPermissions.View, accessResult, ct);
            return Result.Success(await ToDetailDtoAsync(document, content, null, null, ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while requesting a masked-copy re-scan. DocumentId: {DocumentId}", documentId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Looks up everything <see cref="DocumentContentAccessDecision"/> needs for one caller and
    /// one document. For a document that is not restricted this is the evaluator's answer and no
    /// further I/O.
    /// </summary>
    private async Task<DocumentContentAccessInput> ResolveContentAccessAsync(
        Guid userId,
        Guid workspaceId,
        WorkspaceDocument document,
        string permission,
        Result? access,
        CancellationToken ct)
    {
        var granted = access?.IsSuccess == true;
        if (!document.IsRestricted())
        {
            return new DocumentContentAccessInput(false, false, false, granted, granted, false, false);
        }

        var isUploader = document.OwnerId == userId || document.UploadedBy == userId;
        var isOwnerOrAdmin = false;
        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member != null)
        {
            isOwnerOrAdmin = (await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct)).IsOwnerOrAdmin();
        }

        var maskedExists = await SafeMaskedFileExistsAsync(document, ct);

        var grantedIgnoringRestriction = granted;
        if (!granted && !isOwnerOrAdmin && !isUploader)
        {
            var ignoring = await _accessEvaluator.EvaluateAccessIgnoringRestrictionAsync(userId, workspaceId, document.Id, permission, ct);
            grantedIgnoringRestriction = ignoring?.IsSuccess == true;
        }

        var restrictedByPii = maskedExists
            || (!isOwnerOrAdmin && !isUploader && await WasRestrictedByPiiAsync(document.Id, ct));

        return new DocumentContentAccessInput(
            isOwnerOrAdmin, isUploader, true, granted, grantedIgnoringRestriction, maskedExists, restrictedByPii);
    }

    /// <summary>The list's version of the same decision, with the member and role it already loaded.</summary>
    private async Task<DocumentContentVersion> DecideListedVersionAsync(
        Guid userId,
        Guid workspaceId,
        WorkspaceDocument document,
        WorkspaceMember member,
        string roleName,
        IEnumerable<WorkspaceDocumentAccessPolicy> policies,
        Dictionary<Guid, TranslationRoomDto?>? roomCache,
        Dictionary<Guid, List<TranslationRoomParticipantDto>>? participantsCache,
        Result? access,
        CancellationToken ct)
    {
        var granted = access?.IsSuccess == true;
        if (!document.IsRestricted())
        {
            return granted ? DocumentContentVersion.Original : DocumentContentVersion.None;
        }

        var isUploader = document.OwnerId == userId || document.UploadedBy == userId;
        var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
        if (isOwnerOrAdmin || isUploader)
        {
            return DocumentContentAccessDecision.Decide(
                new DocumentContentAccessInput(isOwnerOrAdmin, isUploader, true, granted, granted, false, false));
        }

        var grantedIgnoringRestriction = granted;
        if (!granted)
        {
            var ignoring = await _accessEvaluator.EvaluateAccessIgnoringRestrictionAsync(
                userId, workspaceId, document, WorkspaceDocumentPermissions.View, member, roleName, policies, roomCache, participantsCache, ct);
            grantedIgnoringRestriction = ignoring?.IsSuccess == true;
        }

        if (!grantedIgnoringRestriction)
        {
            return DocumentContentVersion.None;
        }

        var maskedExists = await SafeMaskedFileExistsAsync(document, ct);
        var restrictedByPii = maskedExists || (granted && await WasRestrictedByPiiAsync(document.Id, ct));

        return DocumentContentAccessDecision.Decide(
            new DocumentContentAccessInput(false, false, true, granted, grantedIgnoringRestriction, maskedExists, restrictedByPii));
    }

    /// <summary>
    /// Is there a masked copy? A store that cannot say is treated as "no": a member then gets
    /// nothing, which is the safe side of not knowing.
    /// </summary>
    private async Task<bool> SafeMaskedFileExistsAsync(WorkspaceDocument document, CancellationToken ct)
    {
        try
        {
            return await _storage.MaskedFileExistsAsync(document, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not check for the masked copy of document {DocumentId}.", document.Id);
            return false;
        }
    }

    /// <summary>Did the last security scan restrict this document for personal details, and not for a banned keyword?</summary>
    private async Task<bool> WasRestrictedByPiiAsync(Guid documentId, CancellationToken ct)
    {
        var scan = await _unitOfWork.WorkspaceDocumentAuditRepository.GetLatestActionAsync(
            documentId, WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted, ct);
        return WorkspaceDocumentMapper.ReadScanOutcome(scan?.Metadata).RestrictedByPii;
    }

    /// <summary>
    /// Why the document does or does not have a masked copy, from the audit trail. Storage
    /// decides whether one exists; this only explains it.
    /// </summary>
    private async Task<(bool RestrictedByPii, string Status)> ReadMaskedVersionStateAsync(
        WorkspaceDocument document,
        bool maskedExists,
        CancellationToken ct)
    {
        var audits = _unitOfWork.WorkspaceDocumentAuditRepository;
        var scan = await audits.GetLatestActionAsync(document.Id, WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted, ct);
        var requested = await audits.GetLatestActionAsync(document.Id, WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanRequested, ct);
        var failed = await audits.GetLatestActionAsync(document.Id, WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanFailed, ct);

        var outcome = WorkspaceDocumentMapper.ReadScanOutcome(scan?.Metadata);
        var restrictedByPii = maskedExists || outcome.RestrictedByPii;

        if (requested != null
            && (scan == null || scan.ActionAt < requested.ActionAt)
            && (failed == null || failed.ActionAt < requested.ActionAt))
        {
            return (restrictedByPii, WorkspaceDocumentMaskedVersionStatuses.Pending);
        }

        if (maskedExists)
        {
            return (restrictedByPii, WorkspaceDocumentMaskedVersionStatuses.Available);
        }

        if (failed != null && (scan == null || scan.ActionAt < failed.ActionAt))
        {
            return (restrictedByPii, WorkspaceDocumentMaskedVersionStatuses.Error);
        }

        // "available" on record with no file in storage is a copy that has since gone.
        var recorded = outcome.MaskedVersion;
        var status = string.IsNullOrWhiteSpace(recorded) || recorded == WorkspaceDocumentMaskedVersionStatuses.Available
            ? WorkspaceDocumentMaskedVersionStatuses.NotGenerated
            : recorded;
        return (restrictedByPii, status);
    }

    /// <summary>The detail DTO, told which version this caller gets and what there is to offer them.</summary>
    private async Task<WorkspaceDocumentDto> ToDetailDtoAsync(
        WorkspaceDocument document,
        DocumentContentAccessInput content,
        Guid? approvedBy,
        string? rejectionReason,
        CancellationToken ct)
    {
        var version = DocumentContentAccessDecision.Decide(content);
        var dto = document.ToDto(
            version == DocumentContentVersion.Original
                ? _urlProvider.GetDocumentDownloadUrl(document.WorkspaceId, document.Id)
                : null,
            approvedBy,
            rejectionReason);

        if (!document.IsRestricted())
        {
            return dto;
        }

        var state = await ReadMaskedVersionStateAsync(document, content.MaskedVersionExists, ct);
        return dto with
        {
            ContentAccess = version.ToWireValue(),
            MaskedVersionAvailable = DocumentContentAccessDecision.CanReadMaskedVersion(content),
            MaskedVersionStatus = state.Status,
            CanRescanMaskedVersion = content.IsOwnerOrAdmin
        };
    }

    public async Task<Result> DeleteDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            // Verify user is Owner/Admin or Document Owner
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;

            if (!isOwnerOrAdmin && !isDocOwner)
            {
                return Result.Failure("Forbidden. Only owner, admin, or document owner can delete.", ErrorCodes.Forbidden);
            }

            document.DeletedAt = DateTime.UtcNow;
            document.DeletedBy = userId;
            document.AiEligible = false;
            document.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _eventPublisher.PublishDocumentDeletedAsync(documentId, workspaceId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            // THE VECTORS HAVE TO GO, NOT JUST BE ANNOUNCED (WT-871). The DocumentDeleted
            // invalidation above has no consumer — the event catalog lists `consumers: []`,
            // warptalk-ai never reads `workspace-document-events`, and the only .NET reader of
            // that stream handles DocumentUploaded alone — so on its own it removed nothing. An
            // Owner/Admin's WarpBot search is not narrowed by the ai-retrievable allowlist, so it
            // went on answering from the deleted file, and the Knowledge page (which reads the
            // same Qdrant points, facts included) went on listing it.
            //
            // Same helper and same order as UnpublishDocumentAsync: after the commit, because a
            // purge that ran ahead of a failed save would strip the index of a document that is
            // still live. A purge failure is logged and recorded on the audit row but does not
            // undo the delete — the row is the authority, the allowlist already excludes it, and
            // what lingers is chunks an Owner/Admin can still remove from the Knowledge page.
            var vectorsPurged = await TryPurgeDocumentChunksAsync(workspaceId, documentId, ct);

            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                "deleted",
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Deleted,
                document.UpdatedAt,
                userId,
                ct);

            await _unitOfWork.AuditAsync(
                documentId,
                workspaceId,
                userId,
                WorkspaceDocumentConstants.AuditActions.DeleteDocument,
                new { vectorsPurged },
                _logger,
                ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while deleting document. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ArchiveDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;

            if (!isOwnerOrAdmin && !isDocOwner)
            {
                return Result.Failure("Forbidden. Only owner, admin, or document owner can archive.", ErrorCodes.Forbidden);
            }

            document.Status = WorkspaceDocumentStatus.archived.ToString();
            document.AiEligible = false;
            document.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _eventPublisher.PublishDocumentArchivedAsync(documentId, workspaceId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Archived,
                document.UpdatedAt,
                userId,
                ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.ArchiveDocument, logger: _logger, ct: ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while archiving document. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RestoreDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;

            if (!isOwnerOrAdmin && !isDocOwner)
            {
                var audit = await _unitOfWork.WorkspaceDocumentAuditRepository.FirstOrDefaultAsync(
                    a => a.DocumentId == document.Id && a.Action == WorkspaceDocumentConstants.AuditActions.ArchiveDocument, "", ct);
                var isArchiver = audit != null && audit.ActorId == userId;
                if (!isArchiver)
                {
                    return Result.Failure("Forbidden. Only owner, admin, document owner, or archiver can restore.", ErrorCodes.Forbidden);
                }
            }

            if (document.Status != WorkspaceDocumentStatus.archived.ToString())
            {
                return Result.Failure("Document is not archived.", ErrorCodes.ValidationError);
            }

            document.Status = WorkspaceDocumentStatus.@public.ToString();
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString();
            document.AiEligible = false; // Scanner will re-evaluate on background security scan
            document.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            if (document.IsAiAllowed)
            {
                await _eventPublisher.PublishDocumentUploadedAsync(
                    document.Id,
                    workspaceId,
                    document.StorageKey,
                    document.FileName,
                    document.FileExtension,
                    document.UploadedBy ?? userId,
                    document.ConfidentialityLevel,
                    ct);
            }
            await _unitOfWork.SaveChangesAsync(ct);
            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Restored,
                document.UpdatedAt,
                userId,
                ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.RestoreDocument, logger: _logger, ct: ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while restoring document. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Takes a published document back from the workspace: public → private.
    /// </summary>
    /// <remarks>
    /// WHAT "PUBLIC" WAS, AND WHY IT COULD NOT BE UNDONE
    ///   `public` is the approval state, not a share link — there is no anonymous or tokenised
    ///   document route anywhere in this service. It means every internal member reads the
    ///   document by default, and the assistant may index it. Once approved, the only ways out
    ///   were archive (retire it) and delete; nothing could say "keep it, but stop sharing it".
    ///
    /// WHAT PRIVATE CUTS, SERVER-SIDE
    ///   - Reads: DocumentAccessEvaluator refuses the default audience (403) and ignores the broad
    ///     Role/MembershipType ALLOWs, so list, detail, download, extracted-text and the
    ///     ai-retrievable allowlist all stop returning it to anyone not named.
    ///   - The index: IsIndexEligible requires `public`, so nothing re-indexes it; and the vectors
    ///     already in Qdrant are deleted HERE, synchronously, because the invalidation event this
    ///     service publishes elsewhere has no consumer at all.
    ///
    /// WHO: Owner/Admin, or the document's uploader/owner — the same people who may archive it.
    /// Withdrawing your own contribution only ever narrows access, so it needs no approver.
    /// Idempotent: making a private document private again succeeds without touching anything.
    /// </remarks>
    public async Task<Result<WorkspaceDocumentDto>> UnpublishDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var (loaded, isOwnerOrAdmin, isDocOwner, failure) = await LoadForVisibilityChangeAsync(workspaceId, documentId, userId, ct);
            if (failure != null || loaded == null)
            {
                return failure ?? Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            var document = loaded;

            if (!isOwnerOrAdmin && !isDocOwner)
            {
                return Result.Failure<WorkspaceDocumentDto>(
                    "Forbidden. Only an owner, an admin or the document's uploader can make it private.",
                    ErrorCodes.Forbidden);
            }

            if (document.IsPrivate())
            {
                return Result.Success(document.ToDto(_urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id)));
            }

            if (!document.IsPublic())
            {
                return Result.Failure<WorkspaceDocumentDto>(
                    "Only a published document can be made private.",
                    ErrorCodes.ValidationError);
            }

            var previousStatus = document.Status;
            document.Status = WorkspaceDocumentStatus.@private.ToString();
            document.AiEligible = false;
            // Skipped and un-dated, because after the purge below that is the truth: nothing of
            // this document is in the index. Leaving `completed` + a LastIndexedAt would describe
            // vectors that no longer exist.
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
            document.LastIndexedAt = null;
            document.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);

            // After the commit, not before: the row is the authority on who may read, and a purge
            // that ran ahead of a failed save would strip the assistant of a document that is
            // still public. A purge failure is logged and audited but does not undo the
            // revocation — the evaluator already refuses the reads and the ai-retrievable
            // allowlist already excludes it; what would linger is chunks that only an Owner/Admin
            // can see on the Knowledge page, and they can delete them there.
            var vectorsPurged = await TryPurgeDocumentChunksAsync(workspaceId, documentId, ct);

            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Unpublished,
                document.UpdatedAt,
                userId,
                ct);

            await _unitOfWork.AuditAsync(
                documentId,
                workspaceId,
                userId,
                WorkspaceDocumentConstants.AuditActions.UnpublishDocument,
                new { previousStatus, vectorsPurged },
                _logger,
                ct);

            return Result.Success(document.ToDto(_urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while making document private. DocumentId: {DocumentId}", documentId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Shares a private document with the workspace again.
    /// </summary>
    /// <remarks>
    /// An Owner/Admin publishes it directly — they hold approval authority, and this is the same
    /// transition ApproveDocumentAsync makes, re-ingestion included. The uploader alone does NOT:
    /// taking a document back needs nobody's sign-off, but putting it in front of the whole
    /// workspace is what approval exists to gate, so their document goes back to
    /// `pending_approval` and waits for a reviewer like any other member upload.
    /// </remarks>
    public async Task<Result<WorkspaceDocumentDto>> PublishDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var (loaded, isOwnerOrAdmin, isDocOwner, failure) = await LoadForVisibilityChangeAsync(workspaceId, documentId, userId, ct);
            if (failure != null || loaded == null)
            {
                return failure ?? Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            var document = loaded;

            if (!isOwnerOrAdmin && !isDocOwner)
            {
                return Result.Failure<WorkspaceDocumentDto>(
                    "Forbidden. Only an owner, an admin or the document's uploader can publish it.",
                    ErrorCodes.Forbidden);
            }

            if (document.IsPublic())
            {
                return Result.Success(document.ToDto(_urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id)));
            }

            if (!document.IsPrivate())
            {
                return Result.Failure<WorkspaceDocumentDto>(
                    "Only a private document can be published again.",
                    ErrorCodes.ValidationError);
            }

            string lifecycleEvent;
            document.AiEligible = false;
            if (isOwnerOrAdmin)
            {
                document.Status = WorkspaceDocumentStatus.@public.ToString();
                document.IngestionStatus = document.IsAiAllowed
                    ? WorkspaceDocumentIngestionStatus.pending.ToString()
                    : WorkspaceDocumentIngestionStatus.skipped.ToString();
                lifecycleEvent = WorkspaceDocumentConstants.LifecycleEvents.Published;

                if (document.IsAiAllowed)
                {
                    // The full pipeline, not a bare index request: the security scan decides
                    // again whether this text may be embedded, exactly as it did at approval.
                    await _eventPublisher.PublishDocumentUploadedAsync(
                        document.Id,
                        workspaceId,
                        document.StorageKey,
                        document.FileName,
                        document.FileExtension,
                        document.UploadedBy ?? userId,
                        document.ConfidentialityLevel,
                        ct);
                }
            }
            else
            {
                document.Status = WorkspaceDocumentStatus.pending_approval.ToString();
                document.IngestionStatus = WorkspaceDocumentIngestionStatus.awaiting_approval.ToString();
                lifecycleEvent = WorkspaceDocumentConstants.LifecycleEvents.PendingApproval;
            }

            document.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);

            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                lifecycleEvent,
                document.UpdatedAt,
                userId,
                ct);

            await _unitOfWork.AuditAsync(
                documentId,
                workspaceId,
                userId,
                WorkspaceDocumentConstants.AuditActions.PublishDocument,
                new { status = document.Status },
                _logger,
                ct);

            return Result.Success(document.ToDto(_urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while publishing document. DocumentId: {DocumentId}", documentId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    private async Task<(WorkspaceDocument? Document, bool IsOwnerOrAdmin, bool IsDocOwner, Result<WorkspaceDocumentDto>? Failure)>
        LoadForVisibilityChangeAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct)
    {
        if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
        {
            return (null, false, false, Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound));
        }

        var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
        if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
        {
            return (null, false, false, Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound));
        }

        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member == null)
        {
            return (null, false, false, Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden));
        }

        var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
        var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;
        return (document, roleName.IsOwnerOrAdmin(), isDocOwner, null);
    }

    /// <returns>Whether the store confirmed the delete.</returns>
    private async Task<bool> TryPurgeDocumentChunksAsync(Guid workspaceId, Guid documentId, CancellationToken ct)
    {
        try
        {
            await _chunkWriter.DeleteDocumentChunksAsync(workspaceId, documentId, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not delete indexed chunks of document {DocumentId} in workspace {WorkspaceId}. Access is already revoked; the chunks remain visible on the Knowledge page until deleted.",
                documentId,
                workspaceId);
            return false;
        }
    }

    public async Task<Result<ExtractedTextDto>> GetExtractedTextAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<ExtractedTextDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.View, ct);
            if (!accessResult.IsSuccess)
            {
                return Result.Failure<ExtractedTextDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.DeletedAt != null)
            {
                return Result.Failure<ExtractedTextDto>("Document not found.", ErrorCodes.NotFound);
            }

            // WT-872. This read exists for the assistant — WarpBot's get_document tool is its only
            // caller — and `view` is not the question the assistant needs answered. The uploader
            // holds `view` on their own document from the moment it is uploaded, so a document
            // sitting in Pending Approval, or one an admin had Rejected, was quoted back to them
            // verbatim by WarpBot while the index (correctly) had never seen it.
            //
            // The gate is IsIndexEligible, the one definition of "may the model read this" that
            // the embedding pipeline already uses — not a second list of statuses kept here.
            // Checked AFTER the ACL on purpose: someone who may not see the document at all gets
            // the same answer as before, and only a caller who can see it learns why WarpBot
            // cannot use it.
            // The extracted text is written before the scan runs, so it is the ORIGINAL text.
            // IsIndexEligible below already refuses every restricted document; this says the same
            // thing through the content decision, so the rule "no original for a masked viewer"
            // does not depend on that gate staying the way it is.
            var contentAccess = await ResolveContentAccessAsync(userId, workspaceId, document, WorkspaceDocumentPermissions.View, accessResult, ct);
            if (DocumentContentAccessDecision.Decide(contentAccess) != DocumentContentVersion.Original)
            {
                return Result.Failure<ExtractedTextDto>(WorkspaceConstants.Errors.AccessDeniedOriginalContent, ErrorCodes.Forbidden);
            }

            if (!document.IsIndexEligible())
            {
                return Result.Failure<ExtractedTextDto>(
                    WorkspaceConstants.Errors.DocumentNotAiEligible,
                    WorkspaceDocumentConstants.DocumentNotAiEligibleErrorCode);
            }

            // WT-929. IsIndexEligible is a fact about the DOCUMENT; `ai_retrieval` is the answer
            // for THIS CALLER, and nothing on this read asked it. An owner who set "Deny AI
            // retrieval" for a member (or for the Member role) took the document out of that
            // person's semantic search — ListAiRetrievableDocumentIdsAsync asks the evaluator —
            // while an @document mention, or get_document with an id, still read the whole text
            // back through here on the strength of `view` alone.
            //
            // Same evaluator call, same permission as the ai-retrievable list, so the two cannot
            // disagree: what the assistant may not retrieve for a person it may not quote to them
            // either. That includes the index-side half of the permission (ingestion finished,
            // indexed, AiEligible) — a document the index has not caught up with is one the
            // list does not offer yet, and this read now says the same.
            //
            // The same refusal as above on purpose: the caller already holds `view`, so there is
            // nothing to hide about the document's existence, and one error code means WarpBot
            // has one thing to explain.
            var aiAccess = await _accessEvaluator.EvaluateAccessAsync(
                userId, workspaceId, documentId, WorkspaceDocumentPermissions.AiRetrieval, ct);
            if (!aiAccess.IsSuccess)
            {
                return Result.Failure<ExtractedTextDto>(
                    WorkspaceConstants.Errors.DocumentNotAiEligible,
                    WorkspaceDocumentConstants.DocumentNotAiEligibleErrorCode);
            }

            string extractedText = string.Empty;
            try
            {
                extractedText = await _storage.GetExtractedTextAsync(document, ct);
                if (string.IsNullOrEmpty(extractedText))
                {
                    ExtractedDocumentContent content;
                    using (var decryptedStream = await _storage.GetDecryptedStreamAsync(document, ct))
                    {
                        content = await _textExtractor.ExtractTextAsync(decryptedStream, document.FileExtension, ct);
                    }
                    extractedText = JsonSerializer.Serialize(content);
                    await _storage.SaveExtractedTextAsync(document, extractedText, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not extract or load storage file for DocumentId: {DocumentId}", documentId);
                extractedText = string.Empty;
            }

            ExtractedTextDto textDto;
            if (extractedText.TrimStart().StartsWith("{"))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<ExtractedDocumentContent>(extractedText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    textDto = new ExtractedTextDto(
                        parsed?.FullText ?? string.Empty,
                        parsed?.Pages?.Select(p => new ExtractedPageDto(p.PageNumber, p.Text)).ToList() ?? new(),
                        parsed?.Sheets?.Select(s => new ExtractedSheetDto(s.SheetName, s.Rows)).ToList() ?? new()
                    );
                }
                catch
                {
                    textDto = new ExtractedTextDto(extractedText, new(), new());
                }
            }
            else
            {
                textDto = new ExtractedTextDto(extractedText, new(), new());
            }

            return Result.Success(textDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while retrieving extracted text. DocumentId: {DocumentId}", documentId);
            return Result.Failure<ExtractedTextDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<ExtractedTextDto>> UpdateExtractedTextAsync(Guid workspaceId, Guid documentId, string text, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await IsWorkspaceOperationalAsync(workspaceId, ct))
            {
                return Result.Failure<ExtractedTextDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            // A WRITE, so it is gated like the other writes — not like a read.
            //
            // This asked for `view`, which is the permission every ordinary Internal member holds
            // over every non-sensitive document by default. So anyone who could OPEN a document
            // could overwrite the text of it, and the three lines below then published that text
            // to the embedding index: one member could rewrite what the assistant answers about
            // this document for the entire workspace. That is an indirect prompt-injection channel
            // wearing the shape of a metadata edit.
            //
            // CanManagePoliciesAsync is the same gate PatchDocumentMetadataAsync already uses —
            // workspace Owner/Admin, or the document's own owner — and it is the honest one here,
            // because editing the extracted text IS editing the document as far as every reader
            // downstream is concerned.
            var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
            if (!canManage)
            {
                return Result.Failure<ExtractedTextDto>(
                    "Forbidden. Only workspace Owner/Admin or the document owner can edit extracted text.",
                    ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure<ExtractedTextDto>("Document not found.", ErrorCodes.NotFound);
            }

            var content = new ExtractedDocumentContent { FullText = text };
            var jsonContent = JsonSerializer.Serialize(content);
            await _storage.SaveExtractedTextAsync(document, jsonContent, ct);

            // The FULL index gate, not the two conditions this path used to check.
            //
            // It asked only IsAiAllowed + public, so it re-indexed documents the guardrail refuses:
            // ones staged for deletion, and — the one that matters — ones labelled confidential.
            // A restricted document's vectors are purged when it is relabelled; this endpoint put
            // them straight back, which made the whole confidentiality boundary bypassable by
            // anyone who could edit the text.
            //
            // AND THE SAME SCAN UPLOAD RUNS. The label gate above only holds while the label does:
            // an edited text is new content nobody has looked at, and indexing it raw let PII or a
            // blacklisted term reach the vector store through the one path that skipped the worker.
            var reindexed = false;
            if (document.IsIndexEligible() && text is not null)
            {
                reindexed = await ScanThenIndexEditedTextAsync(document, text, ct);
            }

            // Rewriting the AI-readable body of a document left no trace at all before this. It is
            // the one document write with no reviewable artifact of its own — the blob is
            // overwritten in place — so the audit row is the only record that it happened.
            //
            // Built before the audit call on purpose: `text?.Length` below would otherwise leave
            // `text` in a maybe-null flow state and warn here, and the alternative — dropping the
            // null-conditional — would turn a malformed body into a 500 where it used to be a 200.
            var textDto = new ExtractedTextDto(text, new(), new());

            await _unitOfWork.AuditAsync(
                documentId,
                workspaceId,
                userId,
                WorkspaceDocumentConstants.AuditActions.UpdateExtractedText,
                new { Length = text?.Length ?? 0, Reindexed = reindexed },
                _logger,
                ct);

            return Result.Success(textDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating extracted text. DocumentId: {DocumentId}", documentId);
            return Result.Failure<ExtractedTextDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }
}
