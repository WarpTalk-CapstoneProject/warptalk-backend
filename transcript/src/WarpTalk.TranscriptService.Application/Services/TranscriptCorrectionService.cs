using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Enums;
using WarpTalk.TranscriptService.Domain.Interfaces;
using GetTranslationRoomRequest = WarpTalk.Shared.Protos.GetTranslationRoomRequest;
using TranslationRoomServiceClient = WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient;

namespace WarpTalk.TranscriptService.Application.Services;

public class TranscriptCorrectionService : ITranscriptCorrectionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranscriptReadAccess _readAccess;

    /// <summary>
    /// Still here, and deliberately so: <see cref="FinalizeTranscriptAsync"/> asks a different
    /// question from the read predicate — "are you the host", a write authority — and it has to
    /// tell an absent room (NOT_FOUND) apart from a non-host caller (UNAUTHORIZED). Folding that
    /// into <see cref="ITranscriptReadAccess"/>, which collapses a missing room to a plain
    /// "false", would silently turn one of those responses into the other.
    /// </summary>
    private readonly TranslationRoomServiceClient _roomClient;

    /// <summary>
    /// Owns the one stream a saved transcript's translations are re-requested on. A correction to
    /// what somebody said invalidates every translation of that line, and this is what redoes them.
    /// </summary>
    private readonly ITranscriptTranslationBackfillService _backfillService;
    private readonly ILogger<TranscriptCorrectionService> _logger;

    public TranscriptCorrectionService(
        IUnitOfWork unitOfWork,
        ITranscriptReadAccess readAccess,
        TranslationRoomServiceClient roomClient,
        ITranscriptTranslationBackfillService backfillService,
        ILogger<TranscriptCorrectionService> logger)
    {
        _unitOfWork = unitOfWork;
        _readAccess = readAccess;
        _roomClient = roomClient;
        _backfillService = backfillService;
        _logger = logger;
    }

    public async Task<Result> SubmitCorrectionAsync(Guid transcriptId, Guid segmentId, Guid userId, CreateCorrectionDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var segment = await _unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (segment == null)
                return Result.Failure($"Segment with ID {segmentId} not found.", "NOT_FOUND");

            if (segment.TranscriptId != transcriptId)
                return Result.Failure($"Segment {segmentId} does not belong to transcript {transcriptId}.", "NOT_FOUND");

            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
            if (transcript == null)
                return Result.Failure($"Transcript with ID {segment.TranscriptId} not found.", "NOT_FOUND");

            if (string.Equals(transcript.Status, "ARCHIVED", StringComparison.OrdinalIgnoreCase))
                return Result.Failure("Archived transcripts cannot be corrected.", "BAD_REQUEST");

            if (!await CanAccessTranscriptAsync(transcript, userId, cancellationToken))
                return Result.Failure("You do not have access to this transcript.", "UNAUTHORIZED");

            var correction = dto.ToEntity(segmentId, userId);
            var isMtCorrection = dto.CorrectionType?.Equals("MT", StringComparison.OrdinalIgnoreCase) == true;
            var isSttCorrection = dto.CorrectionType?.Equals("STT", StringComparison.OrdinalIgnoreCase) == true;
            var correctedLanguage = string.Empty;

            // Which languages this line currently reads in. Both correction types need it and both
            // need it BEFORE the save: MT replaces one of these rows, STT invalidates all of them.
            var currentLinks = (await _unitOfWork.SegmentTranslationLinks.FindAsync(
                    l => l.SegmentId == segmentId && l.IsCurrent, cancellationToken))
                .ToList();

            if (isMtCorrection)
            {
                if (string.IsNullOrWhiteSpace(dto.TargetLanguage))
                    return Result.Failure("TargetLanguage is required for MT corrections.", "BAD_REQUEST");

                // Normalized, because a room hands out "en-US" and the link stores "en". Comparing
                // the raw strings found no link, so the correction recorded no row as its subject
                // and — now that it also replaces one — would have replaced nothing.
                var language = TranscriptTranslationBackfillService.NormalizeLanguage(dto.TargetLanguage);
                var currentLink = currentLinks.FirstOrDefault(
                    l => TranscriptTranslationBackfillService.NormalizeLanguage(l.TargetLanguage) == language);

                correction.TranslationContentId = currentLink?.TranslationContentId;
                correctedLanguage = language;
                correction.TriggeredRetranslation = true;

                // ReversalCreditTransactionId is intentionally left null here: reversing the prior
                // TRANSLATION charge requires a Billing gRPC endpoint that can look up and reverse
                // subscription.credit_transactions by (segment_id, target_lang) — that endpoint
                // doesn't exist yet (billing_worker only charges forward, see
                // warptalk-ai/billing_worker/worker.py). Tracked as a follow-up, not fabricated here.
            }
            else if (isSttCorrection)
            {
                segment.OriginalText = dto.CorrectedText;

                // Every translation of this line is now a translation of a sentence nobody said.
                // Recorded truthfully rather than hardcoded: a line with no translations to redo
                // triggers no retranslation, and claiming otherwise is what the column did before.
                correction.TriggeredRetranslation = currentLinks.Count > 0;
            }
            else
            {
                correction.TriggeredRetranslation = false;
            }

            segment.IsCorrected = true;

            _unitOfWork.TranscriptSegments.Update(segment);
            await _unitOfWork.TranscriptCorrections.AddAsync(correction, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            if (isMtCorrection)
            {
                // An MT correction IS the new translation. A person read the machine's output and
                // typed what it should have said, so handing that text back to the machine would
                // discard the very judgement being recorded — this stores it directly instead.
                //
                // After the save, so the correction is on record before anything acts on it.
                await ApplyHumanTranslationAsync(
                    transcript, segmentId, correctedLanguage, dto.CorrectedText, cancellationToken);
            }

            if (isSttCorrection && correction.TriggeredRetranslation)
            {
                // Queued only after the correction is committed, and best-effort by construction:
                // RequestRetranslationAsync swallows and logs, because the edit the user just made
                // is already saved and failing it now would be a worse lie than a stale translation.
                //
                // What used to be here instead was a publish to translate:requests:{roomId} — a
                // stream no worker has ever consumed, carrying no target language, under a comment
                // asserting that translate_worker picked it up. So no correction has ever
                // propagated: the transcript showed the fix and every translation kept the mistake.
                await _backfillService.RequestRetranslationAsync(segmentId, cancellationToken);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting correction for segment {SegmentId}", segmentId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    /// <summary>
    /// A translator model name of "human", so a reader of translation_contents can tell a person's
    /// wording from a model's. The column is NOT NULL and every other writer puts a model id there.
    /// </summary>
    private const string HumanTranslatorModel = "human";

    /// <summary>
    /// Stores a person's corrected translation as the line's current one.
    ///
    /// Deliberately the same three steps the Redis consumer takes for a machine translation —
    /// find-or-create the deduplicated content, flip the old current link off, link the new one —
    /// because <c>segment_translation_links_current_unique_idx</c> allows exactly one current row
    /// per (segment, language) and any other order violates it.
    /// </summary>
    private async Task ApplyHumanTranslationAsync(
        Transcript transcript,
        Guid segmentId,
        string targetLanguage,
        string correctedText,
        CancellationToken cancellationToken)
    {
        var text = correctedText?.Trim() ?? string.Empty;
        if (text.Length == 0 || targetLanguage.Length == 0)
        {
            return;
        }

        var links = (await _unitOfWork.SegmentTranslationLinks.FindAsync(
                l => l.SegmentId == segmentId, cancellationToken))
            .ToList();

        var superseded = links
            .Where(l => l.IsCurrent
                && TranscriptTranslationBackfillService.NormalizeLanguage(l.TargetLanguage) == targetLanguage)
            .ToList();

        var textHash = TranslationTextHash.Of(text);
        var content = (await _unitOfWork.TranslationContents.FindAsync(
                c => c.WorkspaceId == transcript.WorkspaceId
                    && c.TextHash == textHash
                    && c.TargetLanguage == targetLanguage,
                cancellationToken))
            .FirstOrDefault();

        if (content == null)
        {
            content = new TranslationContent
            {
                Id = Guid.NewGuid(),
                WorkspaceId = transcript.WorkspaceId,
                TextHash = textHash,
                TargetLanguage = targetLanguage,
                TranslatedText = text,
                TranslatorModel = HumanTranslatorModel,
                // The two columns transcript.translation_contents has modelled since it was
                // designed and nothing has ever written. A correction is precisely the case they
                // exist for: this row replaces one, and names which.
                IsRetranslated = superseded.Count > 0,
                PreviousTranslationContentId = superseded.FirstOrDefault()?.TranslationContentId,
                Status = "done"
            };
            await _unitOfWork.TranslationContents.AddAsync(content, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Correcting a line back to wording it already had lands here: the dedup found the old row,
        // and its link is already the current one. Nothing to move.
        if (superseded.Count == 1 && superseded[0].TranslationContentId == content.Id)
        {
            return;
        }

        foreach (var link in superseded)
        {
            link.IsCurrent = false;
            _unitOfWork.SegmentTranslationLinks.Update(link);
        }

        var existing = links.FirstOrDefault(l => l.TranslationContentId == content.Id);
        if (existing != null)
        {
            // (segment_id, translation_content_id) is the composite primary key, so a correction
            // that restores an earlier wording has to revive that row rather than insert a second.
            existing.IsCurrent = true;
            existing.TargetLanguage = targetLanguage;
            _unitOfWork.SegmentTranslationLinks.Update(existing);
        }
        else
        {
            await _unitOfWork.SegmentTranslationLinks.AddAsync(new SegmentTranslationLink
            {
                SegmentId = segmentId,
                TranslationContentId = content.Id,
                TargetLanguage = targetLanguage,
                IsCurrent = true,
                // Null, not UtcNow: nothing was delivered. This text was typed after the meeting
                // and no participant ever heard it.
                DeliveredAt = null
            }, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<Result<IEnumerable<TranscriptCorrectionDto>>> GetCorrectionsBySegmentIdAsync(Guid transcriptId, Guid segmentId, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var segment = await _unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (segment == null || segment.TranscriptId != transcriptId)
                return Result.Failure<IEnumerable<TranscriptCorrectionDto>>($"Segment with ID {segmentId} not found.", "NOT_FOUND");

            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
            if (transcript == null)
                return Result.Failure<IEnumerable<TranscriptCorrectionDto>>($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");

            if (!await CanAccessTranscriptAsync(transcript, userId, cancellationToken))
                return Result.Failure<IEnumerable<TranscriptCorrectionDto>>("You do not have access to this transcript.", "UNAUTHORIZED");

            var corrections = await _unitOfWork.TranscriptCorrections.FindAsync(c => c.SegmentId == segmentId, cancellationToken);
            return Result.Success(corrections.Select(c => c.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting corrections for segment {SegmentId}", segmentId);
            return Result.Failure<IEnumerable<TranscriptCorrectionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> FinalizeTranscriptAsync(
        Guid transcriptId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
            if (transcript == null || transcript.DeletedAt != null)
                return Result.Failure($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");

            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = transcript.TranslationRoomId.ToString() },
                cancellationToken: cancellationToken);
            if (!Guid.TryParse(room.HostId, out var hostId) || hostId != userId)
                return Result.Failure("Only the meeting host can finalize the transcript.", "UNAUTHORIZED");

            if (string.Equals(transcript.Status, "ARCHIVED", StringComparison.OrdinalIgnoreCase))
                return Result.Failure("Archived transcripts cannot be finalized.", "BAD_REQUEST");

            if (string.Equals(transcript.Status, "FINALIZED", StringComparison.OrdinalIgnoreCase))
                return Result.Success();

            var now = DateTime.UtcNow;
            transcript.Status = "FINALIZED";
            transcript.IsActive = false;
            transcript.FinalizedAt = now;
            transcript.UpdatedAt = now;
            transcript.UpdatedBy = userId;
            _unitOfWork.Transcripts.Update(transcript);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return Result.Failure("Translation room not found.", "NOT_FOUND");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finalizing transcript {TranscriptId}", transcriptId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    private Task<bool> CanAccessTranscriptAsync(Transcript transcript, Guid userId, CancellationToken cancellationToken)
        => _readAccess.CanReadRoomTranscriptAsync(transcript.TranslationRoomId, userId, cancellationToken);
}
