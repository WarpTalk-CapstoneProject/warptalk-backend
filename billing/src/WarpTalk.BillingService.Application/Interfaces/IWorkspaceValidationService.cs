using System.Security.Claims;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IWorkspaceValidationService
{
    /// <summary>
    /// Validate that the workspace exists and the current user has access/ownership.
    /// Throws ArgumentException for invalid id, UnauthorizedAccessException for access denied.
    /// </summary>
    Task ValidateAsync(Guid workspaceId, ClaimsPrincipal? user, CancellationToken ct = default);
}
