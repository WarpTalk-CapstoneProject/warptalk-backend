using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;

namespace WarpTalk.AuthService.Application.Services;

/// <summary>
/// Turns "the AI side cloned this person" into a row they own (WT-B).
///
/// The row is an ordinary <c>voice_profiles</c> entry, which is what buys a captured clone — for
/// free — the listing, the preview, the delete and the dub-voice picker that uploaded recordings
/// already have. <see cref="VoiceProfileSources.InMeeting"/> is the only thing that tells the two
/// apart, and it decides the one rule they do not share: a captured voice may be replaced by a
/// better capture, an uploaded one never is.
/// </summary>
public class VoiceCarryOverService : IVoiceCarryOverService
{
    /// <summary>Only Cartesia makes these today; stored so a second provider can coexist.</summary>
    private const string Provider = "cartesia";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IVoiceCarryOverQueue _queue;
    private readonly IVoiceConsentService _consent;
    private readonly ILogger<VoiceCarryOverService> _logger;

    public VoiceCarryOverService(
        IUnitOfWork unitOfWork,
        IVoiceCarryOverQueue queue,
        IVoiceConsentService consent,
        ILogger<VoiceCarryOverService> logger)
    {
        _unitOfWork = unitOfWork;
        _queue = queue;
        _consent = consent;
        _logger = logger;
    }

    /// <summary>
    /// Apply one announcement. Idempotent: a redelivered message re-finds its own row and changes
    /// nothing, which is what lets the consumer acknowledge only after committing.
    /// </summary>
    public async Task ApplyAsync(VoiceCarryOverMessage message, CancellationToken ct = default)
    {
        // FAIL CLOSED ON CONSENT, RE-ASKED HERE RATHER THAN TRUSTED FROM THE CAPTURE.
        //
        // The AI side checked consent when it captured the audio; this runs afterwards, and
        // "afterwards" is exactly when somebody who changed their mind mid-meeting would have
        // withdrawn it. Storing the voice anyway would make the withdrawal a lie in the most
        // durable way available — a permanent row.
        //
        // The voice is destroyed rather than merely dropped. It has already been renamed out of
        // the orphan sweep's sights by the producer, so leaving it would leak it forever in the
        // one place the sweep is told never to look.
        if (!await _consent.HasActiveConsentAsync(message.UserId, ct))
        {
            await _queue.RequestDeletionAsync(message.VoiceId, "consent-not-active", ct);
            _logger.LogInformation(
                "Discarded a carried-over voice for {UserId}: no active voice-clone consent.",
                message.UserId);
            return;
        }

        var existing = await _unitOfWork.VoiceProfileRepository.GetAutoCloneAsync(
            message.UserId, message.Language, ct);

        var now = DateTime.UtcNow;

        if (existing is null)
        {
            _unitOfWork.VoiceProfileRepository.Add(new VoiceProfile
            {
                Id = Guid.CreateVersion7(),
                UserId = message.UserId,
                DisplayName = $"My voice ({message.Language})",
                Language = message.Language,
                Provider = Provider,
                EmbeddingRef = message.VoiceId,
                Status = "active",
                Source = VoiceProfileSources.InMeeting,
                QualityScore = message.Score,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });

            await _unitOfWork.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Kept a voice cloned in a meeting for {UserId} ({Language}), score {Score}.",
                message.UserId, message.Language, message.Score);
            return;
        }

        // The same voice arriving again — a redelivery, or two replicas racing. Nothing to do
        // beyond recording a score we may not have had, and nothing to delete.
        if (string.Equals(existing.EmbeddingRef, message.VoiceId, StringComparison.Ordinal))
        {
            if (existing.QualityScore is null && message.Score is not null)
            {
                existing.QualityScore = message.Score;
                existing.UpdatedAt = now;
                _unitOfWork.VoiceProfileRepository.Update(existing);
                await _unitOfWork.SaveChangesAsync(ct);
            }

            return;
        }

        // A DIFFERENT voice for a language we already have one for.
        //
        // The producer only sends a replacement it already judged better against the bar this
        // row set, so accepting is the normal path. It is re-checked here anyway because the
        // producer's bar can be stale: two replicas, or a message delayed behind a deploy, can
        // both have been measured against a score this row has since moved past.
        //
        // An unscored incumbent loses. Unmeasured is not a claim to be good, and refusing to
        // ever replace it would strand people on rows written before scores existed.
        var isBetter = existing.QualityScore is null
            || (message.Score is not null && message.Score > existing.QualityScore);

        if (!isBetter)
        {
            // The loser must be destroyed, not merely ignored. The producer renamed it before
            // announcing it, so the orphan sweep will never collect it — ignoring it here is how
            // promoting clones would move the leak that sweep was built to close into the one
            // place it cannot reach.
            await _queue.RequestDeletionAsync(message.VoiceId, "carry-over-not-an-improvement", ct);
            _logger.LogInformation(
                "Rejected a carried-over voice for {UserId} ({Language}): {NewScore} did not beat "
                + "the stored {OldScore}.",
                message.UserId, message.Language, message.Score, existing.QualityScore);
            return;
        }

        var replaced = existing.EmbeddingRef;

        existing.EmbeddingRef = message.VoiceId;
        existing.QualityScore = message.Score;
        existing.Status = "active";
        existing.IsActive = true;
        existing.UpdatedAt = now;
        _unitOfWork.VoiceProfileRepository.Update(existing);

        // Committed BEFORE the old voice is destroyed. The other order can delete a voice the row
        // still points at, which is the worst state available here: a profile naming an id
        // Cartesia has never heard of, dubbing the person as a stranger while looking correct.
        await _unitOfWork.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(replaced))
        {
            await _queue.RequestDeletionAsync(replaced, "carry-over-replaced", ct);
        }

        _logger.LogInformation(
            "Replaced the carried-over voice for {UserId} ({Language}): score {Score}.",
            message.UserId, message.Language, message.Score);
    }
}
