using System;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Domain.Extensions;

public static class WorkspaceMemberStatusExtensions
{
    public static string ToStorageValue(this WorkspaceMemberStatus status)
        => status.ToString().ToLowerInvariant();

    public static bool IsStatus(this string? value, WorkspaceMemberStatus status)
        => string.Equals(value, status.ToStorageValue(), StringComparison.OrdinalIgnoreCase);
}
