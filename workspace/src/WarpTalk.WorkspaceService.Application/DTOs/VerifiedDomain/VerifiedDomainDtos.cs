using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.VerifiedDomain;

/// <summary>
/// Represents a verified domain associated with a workspace.
/// Domains are trusted immediately upon registration — no DNS challenge required.
/// </summary>
public record VerifiedDomainDto(
    Guid Id,
    string Domain,
    string Status,
    /// <summary>owner_email | self_asserted | dns_txt — see VerifiedDomainVerificationMethods.
    /// The UI badges these differently: an owner_email domain needed no assertion from anyone,
    /// a self_asserted one is only as trustworthy as the Owner who claimed it.</summary>
    string VerificationMethod,
    DateTime VerifiedAt,
    DateTime? RevokedAt,
    DateTime CreatedAt
);

/// <summary>
/// Request body for adding a new verified domain to a workspace.
/// </summary>
/// <param name="ConsentVersion">
/// Required when the domain does not match the caller's own email domain. The version of the
/// consent text ("I confirm my organization owns …") the Owner agreed to before claiming a
/// domain nobody can verify by DNS. Omitted when the domain is the caller's own — there is
/// nothing to consent to when the evidence is the account itself.
/// </param>
public record AddVerifiedDomainRequest(string Domain, string? ConsentVersion = null);
