using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Settings;

public class WorkspaceSystemSettings
{
    public List<string> ReservedSlugs { get; set; } = new();
}
