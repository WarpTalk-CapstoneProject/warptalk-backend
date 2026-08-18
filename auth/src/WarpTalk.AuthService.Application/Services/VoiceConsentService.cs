using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

/// <summary>
/// Every decision writes a row; nothing is ever updated in place. See VoiceConsent for why.
/// </summary>
public class VoiceConsentService : IVoiceConsentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IVoiceCarryOverQueue _voiceDeletions;
    private readonly ILogger<VoiceConsentService> _logger;
    private readonly Func<DateTime> _utcNow;

    public VoiceConsentService(
        IUnitOfWork unitOfWork,
        IVoiceCarryOverQueue voiceDeletions,
        ILogger<VoiceConsentService> logger,
        Func<DateTime>? utcNow = null)
    {
        _unitOfWork = unitOfWork;
        _voiceDeletions = voiceDeletions;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<Result<VoiceConsentStatusDto>> GetStatusAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var current = await _unitOfWork.VoiceConsentRepository.GetCurrentAsync(
            userId, VoiceConsentTypes.VoiceClone, ct);

        return Result.Success(ToDto(current));
    }

    public async Task<Result<VoiceConsentStatusDto>> GrantAsync(
        Guid userId,
        VoiceConsentDecisionContext context,
        CancellationToken ct = default)
    {
        var current = await _unitOfWork.VoiceConsentRepository.GetCurrentAsync(
            userId, VoiceConsentTypes.VoiceClone, ct);

        // Idempotent, and deliberately so: a double-click, a retried request or a second tab must
        // not litter the audit trail with grants that record no new decision. A grant AFTER a
        // revocation is a new decision and does get its own row — that is the case this guard is
        // careful not to swallow.
        if (current is { ConsentStatus: VoiceConsentStatuses.Granted })
        {
            return Result.Success(ToDto(current));
        }

        var now = _utcNow();
        var consent = new VoiceConsent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ConsentType = VoiceConsentTypes.VoiceClone,
            ConsentStatus = VoiceConsentStatuses.Granted,
            ConsentTextVersion = VoiceConsentTextVersions.Current,
            GrantedAt = now,
            IpAddress = Truncate(context.IpAddress, 45),
            UserAgent = Truncate(context.UserAgent, 500),
            CreatedAt = now,
        };

        _unitOfWork.VoiceConsentRepository.Add(consent);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Voice clone consent granted by {UserId} under text version {TextVersion}.",
            userId, consent.ConsentTextVersion);

        return Result.Success(ToDto(consent));
    }

    public async Task<Result<VoiceConsentStatusDto>> RevokeAsync(
        Guid userId,
        VoiceConsentDecisionContext context,
        CancellationToken ct = default)
    {
        var current = await _unitOfWork.VoiceConsentRepository.GetCurrentAsync(
            userId, VoiceConsentTypes.VoiceClone, ct);

        // Nothing to withdraw. Recording a revocation for someone who never granted would put a
        // decision in the audit trail that the person never made.
        if (current is null || current.ConsentStatus != VoiceConsentStatuses.Granted)
        {
            return Result.Success(ToDto(current));
        }

        var now = _utcNow();
        var revocation = new VoiceConsent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            // Carried forward so the withdrawal names the profile it withdrew permission for.
            VoiceProfileId = current.VoiceProfileId,
            ConsentType = VoiceConsentTypes.VoiceClone,
            ConsentStatus = VoiceConsentStatuses.Revoked,
            // The version they had agreed to, not the version current today: this row records the
            // end of THAT agreement.
            ConsentTextVersion = current.ConsentTextVersion,
            GrantedAt = current.GrantedAt,
            RevokedAt = now,
            IpAddress = Truncate(context.IpAddress, 45),
            UserAgent = Truncate(context.UserAgent, 500),
            CreatedAt = now,
        };

        _unitOfWork.VoiceConsentRepository.Add(revocation);

        // WT-B. Withdrawal now has to DESTROY the voices, not merely stop using them.
        //
        // The consent text says the voice "stops being used the moment you withdraw this
        // permission", and until now that was the whole of the promise and it was kept: the gate
        // is fail-closed and re-checked on every synthesis. Nothing was deleted because nothing
        // survived — an in-meeting clone died with its meeting.
        //
        // Keeping clones between meetings is what turns that into a broken promise. A voice model
        // built from somebody's speech, still sitting in the provider's account after they
        // withdrew permission, is precisely the thing the withdrawal was for. So the rows go, and
        // the provider voices behind them are queued for destruction.
        //
        // Only in_meeting rows. An uploaded recording is a separate decision with its own consent
        // and its own delete button, and sweeping it away here would destroy something the person
        // deliberately made and never asked to lose.
        var captured = await _unitOfWork.VoiceProfileRepository.GetAutoClonesAsync(userId, ct);
        var settings = await _unitOfWork.UserSettingRepository.GetByUserIdAsync(userId, ct);
        var doomedVoices = new List<string>();

        foreach (var profile in captured)
        {
            profile.DeletedAt = now;
            profile.DeletedBy = userId;
            profile.IsActive = false;
            profile.UpdatedAt = now;
            _unitOfWork.VoiceProfileRepository.Update(profile);

            if (string.IsNullOrWhiteSpace(profile.EmbeddingRef))
            {
                continue;
            }

            doomedVoices.Add(profile.EmbeddingRef);

            // If they were being dubbed in the voice we are about to destroy, that choice goes
            // too — in the same unit of work, exactly as DeleteProfileAsync does it. Left behind
            // it names a voice Cartesia no longer has, and the person is dubbed as a stranger.
            if (settings is not null
                && string.Equals(settings.DubVoiceId, profile.EmbeddingRef, StringComparison.Ordinal))
            {
                settings.DubVoiceId = null;
                settings.UpdatedAt = now;
            }
        }

        await _unitOfWork.SaveChangesAsync(ct);

        // AFTER the commit, and that order is deliberate. Asking first and failing to commit
        // would destroy a voice a live row still names. This way the worst case is a voice that
        // outlives its row, which the log below makes findable.
        foreach (var voiceId in doomedVoices)
        {
            await _voiceDeletions.RequestDeletionAsync(voiceId, "consent-revoked", ct);
        }

        // Deliberately loud. Withdrawal of biometric consent is the event most likely to be
        // asked about later, and it is the one that stops a feature working for somebody.
        _logger.LogInformation(
            "Voice clone consent REVOKED by {UserId}; the grant it ends was made at {GrantedAt}. "
            + "{ProfileCount} captured voice profile(s) deleted, {VoiceCount} provider voice(s) "
            + "queued for destruction.",
            userId, current.GrantedAt, captured.Count, doomedVoices.Count);

        return Result.Success(ToDto(revocation));
    }

    public async Task<bool> HasActiveConsentAsync(Guid userId, CancellationToken ct = default)
    {
        var current = await _unitOfWork.VoiceConsentRepository.GetCurrentAsync(
            userId, VoiceConsentTypes.VoiceClone, ct);

        // Anything that is not an active grant is a no: never asked, withdrawn, or expired. The
        // default has to be refusal — this gate stands in front of biometric processing, and a
        // gate that opens when it is unsure is not a gate.
        return current is { ConsentStatus: VoiceConsentStatuses.Granted };
    }

    private static VoiceConsentStatusDto ToDto(VoiceConsent? consent)
    {
        if (consent is null)
        {
            return new VoiceConsentStatusDto(
                HasDecided: false,
                IsGranted: false,
                Status: null,
                ConsentTextVersion: null,
                GrantedAt: null,
                RevokedAt: null);
        }

        return new VoiceConsentStatusDto(
            HasDecided: true,
            IsGranted: consent.ConsentStatus == VoiceConsentStatuses.Granted,
            Status: consent.ConsentStatus,
            ConsentTextVersion: consent.ConsentTextVersion,
            GrantedAt: consent.GrantedAt,
            RevokedAt: consent.RevokedAt);
    }

    /// <summary>A header long enough to overflow the column must not fail the consent it
    /// accompanies — the decision matters more than the evidence about the browser.</summary>
    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Length <= max ? value : value[..max];
    }
}
