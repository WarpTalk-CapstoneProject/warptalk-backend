using System.Text.RegularExpressions;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The verified-domain policy mirror is recomputed from COMMITTED rows, never from pending ones.
///
/// `workspaces.require_verified_domain_for_internal` is derived: it is true exactly when the
/// workspace has at least one active verified domain. RecomputeDomainPolicyAsync owns that
/// invariant and counts the domains through GetActiveVerifiedDomainsAsync — which is a database
/// query, because GenericRepository.FindAsync is `_dbSet.Where(predicate).ToListAsync()`.
///
/// Both mutation paths called it BEFORE SaveChangesAsync, so the count never included the change
/// being made:
///   - add: the new row is pending in the change tracker and absent from the database, so it was
///     not counted and the policy never switched ON;
///   - revoke: the revocation is a pending UPDATE, so the row still satisfies `RevokedAt == null`
///     in the database and was still counted, so the policy never switched OFF.
///
/// This is not a cosmetic flag. UpdateWorkspaceSettings refuses any request whose
/// RequireVerifiedDomainForInternal disagrees with the real domain count, and the settings PATCH
/// merges the stored value into the document it validates — so a workspace left in the drifted
/// state cannot save ANY setting, and the error it shows names verified domains rather than
/// whatever the Owner was actually editing. Observed in production on warptalk-demo-sep490.
///
/// Asserted on the source because the defect is an ORDER of two calls. A test with a mocked
/// repository cannot show it: the mock answers from a list, so the pending row is visible to the
/// count and the bug disappears exactly where it needs to be visible.
/// </summary>
public sealed class VerifiedDomainPolicyOrderingTests
{
    private const string ServicePath =
        "workspace/src/WarpTalk.WorkspaceService.Application/Services/VerifiedDomainService.cs";

    [Theory]
    [InlineData("AddDomainAsync")]
    [InlineData("RevokeDomainAsync")]
    public void ThePolicyIsRecomputedOnlyAfterTheChangeIsCommitted(string method)
    {
        var body = MethodBody(method);

        var firstSave = body.IndexOf("SaveChangesAsync", StringComparison.Ordinal);
        var recompute = body.IndexOf("RecomputeDomainPolicyAsync", StringComparison.Ordinal);

        Assert.True(recompute > 0, $"{method} must recompute the derived policy.");
        Assert.True(firstSave > 0, $"{method} must persist its change.");
        Assert.True(
            firstSave < recompute,
            $"{method} must persist its change BEFORE recomputing the policy — the recompute "
                + "counts domains with a database query, which cannot see a pending row.");
    }

    [Theory]
    [InlineData("AddDomainAsync")]
    [InlineData("RevokeDomainAsync")]
    public void TheRecomputedPolicyIsItselfPersisted(string method)
    {
        // Recomputing after the first save is only half of it: the mirror it writes onto the
        // workspace entity needs a save of its own, or the correction is discarded.
        var body = MethodBody(method);
        var recompute = body.IndexOf("RecomputeDomainPolicyAsync", StringComparison.Ordinal);
        var saveAfter = body.IndexOf("SaveChangesAsync", recompute, StringComparison.Ordinal);

        Assert.True(saveAfter > recompute, $"{method} must persist the recomputed policy.");
    }

    [Fact]
    public void NothingAssignsTheMirrorOutsideTheOneWriter()
    {
        // The helper's own doc says "no path sets the column directly. Three copies of one
        // invariant is how WT-179 happened the first time." Assigning it here would be a fourth.
        var source = Source(ServicePath);

        Assert.DoesNotContain("RequireVerifiedDomainForInternal =", source, StringComparison.Ordinal);
    }

    private static string MethodBody(string method)
    {
        var source = Source(ServicePath);
        var code = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        code = Regex.Replace(code, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

        // The DECLARATION, not the first mention. AddDomainAsync has a delegating overload
        // declared above the real one, and anchoring on the name alone sliced that two-line
        // forwarder instead — which contains neither call, so the assertions passed nothing and
        // failed for a reason that had nothing to do with the code under test.
        // `Task<[^>]*>` does not survive a nested generic: the real declaration returns
        // Task<Result<VerifiedDomainDto>>, and the class stops at the first `>`. Matching to the
        // end of the line instead.
        var declaration = Regex.Match(
            code, $@"public\s+async\s+Task[^\n]*?\b{Regex.Escape(method)}\s*\(");
        Assert.True(declaration.Success, $"{method} declaration not found.");

        var start = declaration.Index;
        // To the start of the next method declaration, or the end of the file.
        var next = Regex.Match(code[(start + declaration.Length)..], @"\n    public\s");
        return next.Success
            ? code.Substring(start, declaration.Length + next.Index)
            : code[start..];
    }

    private static string Source(string relativePath)
    {
        foreach (var startDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startDir);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}
