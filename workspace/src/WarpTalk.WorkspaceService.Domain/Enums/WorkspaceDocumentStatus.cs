using System.Text.Json.Serialization;

namespace WarpTalk.WorkspaceService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceDocumentStatus
{
    @public,
    pending_approval,
    rejected,
    archived,

    /// <summary>
    /// Approved once, then taken back from the workspace. Only Owners, Admins, the uploader and
    /// the people named in a User ALLOW policy may open it; the assistant may not read it at all.
    /// </summary>
    /// <remarks>
    /// "Public" in this codebase never meant a share link or anonymous access — there is no
    /// anonymous document route. It is the approval state: every internal member of the workspace
    /// may read the document by default. Before this value existed a published document could only
    /// leave that state by being archived or deleted, which is why nothing on the detail page could
    /// take "public" back.
    /// </remarks>
    @private
}
