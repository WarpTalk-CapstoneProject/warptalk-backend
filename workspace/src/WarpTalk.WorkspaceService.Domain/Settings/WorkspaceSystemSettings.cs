using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Settings;

public class WorkspaceSystemSettings
{
    public List<string> ReservedSlugs { get; set; } = new();
}
