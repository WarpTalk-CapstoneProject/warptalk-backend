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

    void Add(VoiceConsent entity);
}
