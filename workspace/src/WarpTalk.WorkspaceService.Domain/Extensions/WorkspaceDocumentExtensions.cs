using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Extensions;

public static class WorkspaceDocumentExtensions
{
    public static bool IsRestricted(this WorkspaceDocument document)
    {
        return string.Equals(
            document.ConfidentialityLevel,
            WorkspaceDocumentConstants.SensitiveConfidentialityLevel,
            StringComparison.OrdinalIgnoreCase);
    }
}
