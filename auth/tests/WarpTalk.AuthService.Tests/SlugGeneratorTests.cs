using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.AuthService.Tests;

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
}
