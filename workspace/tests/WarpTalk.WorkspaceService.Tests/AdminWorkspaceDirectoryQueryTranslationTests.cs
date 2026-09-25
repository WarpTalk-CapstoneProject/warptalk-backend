using System;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.ReadModels;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using WarpTalk.WorkspaceService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The admin directory's new filter and sort keys must be SQL PostgreSQL can be asked, and must
/// be applied there rather than after the rows are loaded. ToQueryString raises a translation
/// failure without a database; the Docker-backed AdminWorkspaceDirectoryIntegrationTests run the
/// same query against real rows.
/// </summary>
public class AdminWorkspaceDirectoryQueryTranslationTests
{
    private static readonly DateTime From = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static WorkspaceRepository Repository() =>
        new(new WorkspaceDbContext(new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options));

    private static WorkspaceDirectoryFilter Filter(
        string sort = WorkspaceDirectorySort.CreatedDesc,
        DateTime? createdFrom = null,
        DateTime? createdTo = null) =>
        new(1, 20, null, WorkspaceLifecycleStatus.All, null, null, sort, createdFrom, createdTo);

    [Fact]
    public void CreatedBounds_AreAHalfOpenWindowInTheWhereClause()
    {
        var sql = Repository().AdminDirectoryQuery(Filter(createdFrom: From, createdTo: To)).ToQueryString();

        Assert.Contains("created_at >= @", sql);
        Assert.Contains("created_at < @", sql);
        Assert.DoesNotContain("created_at <= @", sql);
    }

    [Fact]
    public void NoCreatedBounds_AddNoPredicate()
    {
        var sql = Repository().AdminDirectoryQuery(Filter()).ToQueryString();

        Assert.DoesNotContain("created_at >=", sql);
        Assert.DoesNotContain("created_at <", sql);
    }

    [Fact]
    public void UpdatedAsc_OrdersByUpdatedAtAscendingThenId()
    {
        var sql = Repository().AdminDirectoryQuery(Filter(sort: WorkspaceDirectorySort.UpdatedAsc)).ToQueryString();

        var orderBy = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.Matches(@"ORDER BY \w+\.updated_at, \w+\.id", orderBy);
    }

    [Fact]
    public void DefaultSort_IsStillNewestCreatedFirst()
    {
        var sql = Repository().AdminDirectoryQuery(Filter()).ToQueryString();

        var orderBy = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.Matches(@"ORDER BY \w+\.created_at DESC, \w+\.id", orderBy);
    }
}
