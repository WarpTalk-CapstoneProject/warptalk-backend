namespace WarpTalk.WorkspaceService.Domain.Enums;

/// <summary>
/// Status of a workspace verified domain entry.
/// Domains are trusted immediately upon registration (no DNS challenge),
/// so there is no "Pending" state.
/// </summary>
public enum VerifiedDomainStatus
{
    Verified,
    Revoked
}
