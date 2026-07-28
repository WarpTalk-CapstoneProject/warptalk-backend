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
    DateTime VerifiedAt,
    DateTime? RevokedAt,
    DateTime CreatedAt
);

/// <summary>Request body for adding a new verified domain to a workspace.</summary>
public record AddVerifiedDomainRequest(string Domain);
