using System;
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
    private readonly ILogger<VoiceConsentService> _logger;
    private readonly Func<DateTime> _utcNow;

    public VoiceConsentService(
        IUnitOfWork unitOfWork,
        ILogger<VoiceConsentService> logger,
        Func<DateTime>? utcNow = null)
    {
        _unitOfWork = unitOfWork;
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
        await _unitOfWork.SaveChangesAsync(ct);

        // Deliberately loud. Withdrawal of biometric consent is the event most likely to be
        // asked about later, and it is the one that stops a feature working for somebody.
        _logger.LogInformation(
            "Voice clone consent REVOKED by {UserId}; the grant it ends was made at {GrantedAt}.",
            userId, current.GrantedAt);

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
