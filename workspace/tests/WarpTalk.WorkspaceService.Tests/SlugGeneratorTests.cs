using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class SlugGeneratorTests
{
    private readonly IWorkspaceRepository _workspaceRepository;

    public SlugGeneratorTests()
    {
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
    }

    [Theory]
    [InlineData("Google DeepMind", "google-deepmind")]
    [InlineData("WarpTalk! Web-App", "warptalk-web-app")]
    [InlineData("  Space  Out  ", "space-out")]
    [InlineData("C# & .NET 10", "c-sharp-and-net-10")]
    [InlineData("Tiếng Việt Có Dấu", "tieng-viet-co-dau")]
    public void GenerateSlug_ShouldReturnCorrectSlugFormat(string input, string expected)
    {
        // Act
        var result = SlugHelper.GenerateSlug(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ResolveSlugCollisionAsync_ShouldAppendSuffix_WhenSlugExists()
    {
        // Arrange
        var baseSlug = "warptalk-dev";
        _workspaceRepository.AnyAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Workspace, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                var expr = x.ArgAt<System.Linq.Expressions.Expression<Func<Workspace, bool>>>(0);
                var compiled = expr.Compile();

                // Simulate that "warptalk-dev" and "warptalk-dev-1" exist, but "warptalk-dev-2" does not.
                var workspace1 = new Workspace { Slug = "warptalk-dev" };
                var workspace2 = new Workspace { Slug = "warptalk-dev-1" };

                return compiled(workspace1) || compiled(workspace2);
            });

        // Act
        var resolvedSlug = await SlugHelper.ResolveSlugCollisionAsync(baseSlug, _workspaceRepository);

        // Assert
        Assert.Equal("warptalk-dev-2", resolvedSlug);
    }

    /// <summary>
    /// The slug is derived from a name that may be up to WorkspaceNameMaxLength (150), but the
    /// column holding it is WorkspaceSlugMaxLength (100) — so the slug is what overflows first,
    /// on a name the validator was right to accept.
    /// </summary>
    /// <summary>
    /// The number that matters is the web client's, not the column's.
    ///
    /// workspaces.slug is varchar(100), so a 64-to-100 character slug stores perfectly — and then
    /// the web routes on `^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$`, a DNS label. normalizeWorkspaceSlug
    /// returns null for anything longer, and the workspace layout redirects its own owner away.
    /// A workspace that can be created and never opened is worse than one that fails to create.
    /// </summary>
    [Fact]
    public void TheSlugLimit_IsTheOneTheWebCanActuallyRoute()
    {
        const int dnsLabelLimit = 63;

        Assert.Equal(dnsLabelLimit, WorkspaceConstants.WorkspaceSlugMaxLength);
        Assert.True(
            WorkspaceConstants.WorkspaceSlugMaxLength < 100,
            "bounding the slug by the varchar(100) column produces workspaces the web cannot open");
    }

    [Fact]
    public void AGeneratedSlug_MatchesTheWebClientsRoutingPattern()
    {
        // The same expression src/lib/workspace/workspace-slug.ts uses. If a generated slug fails
        // this, the workspace exists and nobody can reach it.
        var routable = new System.Text.RegularExpressions.Regex(
            "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$");

        var slug = SlugHelper.GenerateSlug(new string('a', WorkspaceConstants.WorkspaceNameMaxLength));

        Assert.Matches(routable, slug);
    }

    [Fact]
    public void GenerateSlug_ShouldNeverExceedTheColumnItIsStoredIn()
    {
        var longestAllowedName = new string('a', WorkspaceConstants.WorkspaceNameMaxLength);

        var slug = SlugHelper.GenerateSlug(longestAllowedName);

        Assert.Equal(WorkspaceConstants.WorkspaceSlugMaxLength, slug.Length);
    }

    [Fact]
    public void GenerateSlug_ShouldNotLeaveATrailingHyphen_WhenTheCutFallsOnOne()
    {
        // "aaa… b" slugifies to "aaa…-b"; cutting at the limit can land exactly on that hyphen,
        // which would read as a slug that had been chopped and would double up against a
        // collision suffix.
        var name = new string('a', WorkspaceConstants.WorkspaceSlugMaxLength - 1) + " b";

        var slug = SlugHelper.GenerateSlug(name);

        Assert.DoesNotContain("--", slug);
        Assert.False(slug.EndsWith('-'), "a generated slug must not end with a hyphen");
    }

    /// <summary>
    /// The collision case is the one that reached production behaviour: a name comfortably inside
    /// every rule, whose slug happened to be taken, produced base + "-1" — one character past the
    /// column. It failed only on collision, which is the worst kind of bug to reproduce.
    /// </summary>
    [Fact]
    public async Task ResolveSlugCollisionAsync_ShouldStayInsideTheColumn_WhenTheSuffixIsAppended()
    {
        var baseSlug = new string('a', WorkspaceConstants.WorkspaceSlugMaxLength);
        _workspaceRepository.AnyAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Workspace, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                var compiled = x.ArgAt<System.Linq.Expressions.Expression<Func<Workspace, bool>>>(0).Compile();
                // Only the untouched base is taken, so exactly one suffix is needed.
                return compiled(new Workspace { Slug = baseSlug });
            });

        var resolved = await SlugHelper.ResolveSlugCollisionAsync(baseSlug, _workspaceRepository);

        Assert.True(
            resolved.Length <= WorkspaceConstants.WorkspaceSlugMaxLength,
            $"slug was {resolved.Length} characters, column holds {WorkspaceConstants.WorkspaceSlugMaxLength}");
        Assert.EndsWith("-1", resolved);
    }

    [Fact]
    public async Task ResolveSlugCollisionAsync_ShouldStayInsideTheColumn_AcrossAWideSuffix()
    {
        // A two-digit suffix takes one more character than a one-digit suffix, so the room
        // reserved has to be computed per attempt rather than assumed.
        var baseSlug = new string('a', WorkspaceConstants.WorkspaceSlugMaxLength);
        _workspaceRepository.AnyAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Workspace, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                var compiled = x.ArgAt<System.Linq.Expressions.Expression<Func<Workspace, bool>>>(0).Compile();
                for (var i = 1; i <= 11; i++)
                {
                    var room = WorkspaceConstants.WorkspaceSlugMaxLength - $"-{i}".Length;
                    if (compiled(new Workspace { Slug = baseSlug[..room] + $"-{i}" })) return true;
                }
                return compiled(new Workspace { Slug = baseSlug });
            });

        var resolved = await SlugHelper.ResolveSlugCollisionAsync(baseSlug, _workspaceRepository);

        Assert.True(
            resolved.Length <= WorkspaceConstants.WorkspaceSlugMaxLength,
            $"slug was {resolved.Length} characters, column holds {WorkspaceConstants.WorkspaceSlugMaxLength}");
        Assert.EndsWith("-12", resolved);
    }
}
