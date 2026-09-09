using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Extensions;

public static class WorkspaceExtensions
{
    /// <summary>
    /// Is this workspace one whose data may still be served?
    /// </summary>
    /// <remarks>
    /// `workspace.workspaces` carries exactly two lifecycle columns, so a workspace is in exactly
    /// one of three states: active, suspended (<c>IsActive = false</c>), or deleted
    /// (<c>DeletedAt</c> set). Only the first may be served.
    ///
    /// Deletion mostly enforces itself — both delete paths stamp <c>RemovedAt</c> on every member
    /// (WT-417), so membership lookups fail closed straight after. SUSPENSION does not: it flips
    /// this one flag and leaves every membership row live, which is why anything that authorizes
    /// by membership alone keeps answering for a workspace an admin has just cut off.
    ///
    /// Suspension is the lever for non-payment, abuse and legal hold. A check that a caller is
    /// still a member does not answer the question it asks.
    /// </remarks>
    public static bool IsOperational(this Workspace workspace)
    {
        return workspace.IsActive && workspace.DeletedAt == null;
    }
}
