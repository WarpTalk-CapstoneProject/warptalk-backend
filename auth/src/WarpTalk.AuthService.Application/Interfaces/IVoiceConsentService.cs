using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// Owns the record of who has agreed to have their voice cloned.
///
/// AuthService owns it because the `voice` schema lives in this service's database, beside the
/// voice profiles the consent authorises. Every other service asks; none of them stores its own
/// copy, because a permission with two homes has two answers.
/// </summary>
public interface IVoiceConsentService
{
    /// <summary>What this person has decided, and when. Never null — a person who has never been
    /// asked gets a status saying exactly that, which is different from having said no.</summary>
    Task<Result<VoiceConsentStatusDto>> GetStatusAsync(Guid userId, CancellationToken ct = default);

    Task<Result<VoiceConsentStatusDto>> GrantAsync(
        Guid userId,
        VoiceConsentDecisionContext context,
        CancellationToken ct = default);

    Task<Result<VoiceConsentStatusDto>> RevokeAsync(
        Guid userId,
        VoiceConsentDecisionContext context,
        CancellationToken ct = default);

    /// <summary>
    /// The question every other service actually asks, reduced to a bool. Separate from
    /// GetStatusAsync so the gRPC path cannot accidentally start depending on the audit fields.
    /// </summary>
    Task<bool> HasActiveConsentAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Where a decision came from. Carried as evidence that a specific person, at a specific moment,
/// agreed to specific wording — which is the whole difference between a consent record and a
/// boolean column.
/// </summary>
public readonly record struct VoiceConsentDecisionContext(string? IpAddress, string? UserAgent);
