using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace WarpTalk.AuthService.Tests.Architecture;

/// <summary>
/// G10: no production code in any service may decide admin access from a role CLAIM.
///
/// The role "admin" is still in every staff member's token — the web reads it to know whether to
/// show the portal — but it outlives a suspension or removal by up to the token's lifetime, and it
/// is the same for a Read-only Auditor and a Super Admin. So `IsInRole("admin")`, `Roles =
/// AdminSystem` and the old system-admin policy are all ways to reintroduce the hole G10 closed:
/// an admin power that ignores the permission matrix and cannot be revoked. Use
/// [RequirePermission] on an endpoint, or IStaffAccessResolver.StaffOverrideAllowsAsync for a staff
/// override on an ordinary endpoint.
///
/// A source scan rather than a reflection test because the checks live inside method bodies.
/// </summary>
public sealed class NoLegacyAdminRoleChecksTests
{
    private static readonly Regex[] Forbidden =
    [
        new(@"IsInRole\(\s*(""admin""|""Admin""|WorkspaceRoleConstants\.(SystemAdmin|Admin))\s*\)"),
        new(@"Roles\s*=\s*(WorkspaceRoleConstants\.(AdminSystem|OwnerAdminSystem|SystemAdmin)|""[^""]*\badmin\b[^""]*"")"),
        new(@"SystemAdminAuthorization\.PolicyName"),
        new(@"AddWarpTalkSystemAdminAuthorization"),
    ];

    // The definition of the policy itself, and the scanner that refuses it on endpoints.
    private static readonly string[] Allowed =
    [
        Path.Combine("shared", "WarpTalk.Shared", "Authorization", "SystemAdminAuthorization.cs"),
        Path.Combine("shared", "WarpTalk.Shared", "Authorization", "AdminEndpointPermissionCoverage.cs"),
    ];

    [Fact]
    public void NoServiceDecidesAdminAccessFromARoleClaim()
    {
        var root = RepoRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => IsProductionSource(root, path))
            .Where(path => !Allowed.Any(allowed => path.EndsWith(allowed, StringComparison.Ordinal)))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, number: index + 1)))
            .Where(entry => !entry.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(entry => Forbidden.Any(pattern => pattern.IsMatch(entry.line)))
            .Select(entry => $"{Path.GetRelativePath(root, entry.path)}:{entry.number}: {entry.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static bool IsProductionSource(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.Contains("/bin/") || relative.Contains("/obj/") || relative.Contains("/tests/")) return false;
        return relative.StartsWith("shared/", StringComparison.Ordinal) || relative.Contains("/src/");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find warptalk-backend.slnx above the test output directory.");
    }
}
