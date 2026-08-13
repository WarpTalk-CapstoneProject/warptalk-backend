using System;
using WarpTalk.WorkspaceService.Application.DTOs.VerifiedDomain;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Application.Mappers.VerifiedDomain;

public static class VerifiedDomainMapper
{
    public static VerifiedDomainDto ToDto(this WorkspaceVerifiedDomain entity)
    {
        return new VerifiedDomainDto(
            Id: entity.Id,
            Domain: entity.Domain,
            Status: entity.Status,
            VerificationMethod: entity.VerificationMethod,
            VerifiedAt: entity.VerifiedAt!.Value,
            RevokedAt: entity.RevokedAt,
            CreatedAt: entity.CreatedAt
        );
    }

    /// <param name="verificationMethod">
    /// One of <see cref="VerifiedDomainVerificationMethods"/> — which trust tier backs this row.
    /// </param>
    /// <param name="consentEvidence">
    /// For <see cref="VerifiedDomainVerificationMethods.SelfAsserted"/>, the version of the
    /// consent text the Owner agreed to (e.g. "2026-08-13"), recorded on the row itself so the
    /// evidence is written in the same INSERT as the claim it backs — not in a separate audit
    /// call that could succeed or fail independently of it. For every other tier, a fixed marker;
    /// there is nothing to consent to.
    ///
    /// Reuses the <c>verification_token</c> column: WT-157 planned it for a DNS TXT token, which
    /// no path issues yet, and a free-text slot for "what was agreed to" is the same shape.
    /// </param>
    public static WorkspaceVerifiedDomain ToEntity(
        Guid workspaceId,
        string domain,
        Guid userId,
        string verificationMethod,
        string consentEvidence)
    {
        var now = DateTime.UtcNow;
        return new WorkspaceVerifiedDomain
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Domain = domain,
            Status = VerifiedDomainStatus.Verified.ToString().ToLower(),
            VerificationMethod = verificationMethod,
            VerificationToken = consentEvidence,
            VerifiedAt = now,
            VerifiedBy = userId,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
            UpdatedBy = userId
        };
    }
}
