using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IVoiceConsentRepository : IGenericRepository<VoiceConsent>
{
    /// <summary>
    /// The person's most recent decision of this type, or null if they have never made one.
    ///
    /// "Most recent" is by created_at, not by status: the table is append-only, so a grant that
    /// followed a revocation is the answer and a revocation that followed a grant is equally the
    /// answer. Asking for "the GRANTED row" would find a year-old grant that has since been
    /// withdrawn.
    /// </summary>
    Task<VoiceConsent?> GetCurrentAsync(Guid userId, string consentType, CancellationToken ct = default);

    /// <summary>Every decision this person has made, newest first — the audit trail itself.</summary>
    Task<IReadOnlyList<VoiceConsent>> GetHistoryAsync(Guid userId, string consentType, CancellationToken ct = default);

    /// <summary>
    /// Where the platform's voice consent stands right now, in aggregate.
    ///
    /// COUNTS ONLY. No user ids leave this method, and the shape of the return type is what
    /// enforces that: a per-person list of who has consented to having their voice cloned is a
    /// register of biometric permissions, and nothing on the screen this feeds acts on a person.
    ///
    /// "Right now" means the newest row per (user, consent type) — the table is append-only, so a
    /// grant that followed a revocation is the answer and counting GRANTED rows would report
    /// everyone who has ever agreed, including those who have since withdrawn.
    /// </summary>
    Task<AdminVoiceConsentSnapshot> GetAdminSnapshotAsync(CancellationToken ct = default);

    void Add(VoiceConsent entity);
}

/// <summary>How many people currently sit at each status, and under which wording.</summary>
/// <param name="ByStatus">
/// Keyed by (consent type, status). Sums to the number of people who have ever made a decision.
/// </param>
/// <param name="CurrentGrantsByTextVersion">
/// Which version of the consent text the people who currently have a grant agreed to. This is the
/// question the version column was added to answer: after the wording changes, how many live
/// grants are still against the old one.
/// </param>
/// <param name="TotalDecisions">Rows in the table — the length of the audit trail, not people.</param>
public sealed record AdminVoiceConsentSnapshot(
    IReadOnlyList<AdminVoiceConsentStatusCount> ByStatus,
    IReadOnlyList<AdminVoiceConsentVersionCount> CurrentGrantsByTextVersion,
    int TotalDecisions);

public sealed record AdminVoiceConsentStatusCount(string ConsentType, string Status, int People);

public sealed record AdminVoiceConsentVersionCount(string TextVersion, int People);
