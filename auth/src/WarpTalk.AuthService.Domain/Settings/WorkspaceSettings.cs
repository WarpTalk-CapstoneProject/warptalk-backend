using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Settings;

public class WorkspaceSettings
{
    public List<string> ReservedSlugs { get; set; } = new();
}
