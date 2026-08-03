using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Integration;

/// <summary>
/// Exercises the admin directory against real PostgreSQL so the projection, filters, sorting,
/// and paging are proven to translate to SQL — a mocked repository cannot show that.
/// </summary>
public class AdminWorkspaceDirectoryIntegrationTests : BaseIntegrationTest
{
    private const string AdminRole = "admin";

    private readonly Guid _adminUserId = Guid.NewGuid();
    private readonly Guid _outsiderUserId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    private HttpClient AdminClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(_adminUserId, "root@warptalk.io.vn", AdminRole));
        return client;
    }

    private HttpClient MemberClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GenerateJwtToken(_outsiderUserId, "member@acme.com"));
        return client;
    }

    private async Task<(Guid ActiveId, Guid SuspendedId)> SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();

        var activeId = Guid.NewGuid();
        var suspendedId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Workspaces.AddRange(
            new Workspace
            {
                Id = activeId,
                Name = "Acme Localization",
                Slug = "acme-localization",
                OwnerId = _ownerId,
                Settings = "{}",
                IsActive = true,
                CreatedAt = now.AddDays(-10),
                UpdatedAt = now.AddDays(-2),
            },
            new Workspace
            {
                Id = suspendedId,
                Name = "Zenith Media",
                Slug = "zenith-media",
                OwnerId = _ownerId,
                Settings = "{}",
                IsActive = false,
                CreatedAt = now.AddDays(-20),
                UpdatedAt = now.AddDays(-1),
            });

        db.WorkspaceMembers.AddRange(
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = activeId,
                UserId = _ownerId,
                RoleId = Guid.NewGuid(),
                MembershipType = MembershipType.Internal.ToString(),
                Status = WorkspaceMemberStatus.Active.ToString(),
                JoinedAt = now.AddDays(-9),
            },
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = activeId,
                UserId = Guid.NewGuid(),
                RoleId = Guid.NewGuid(),
                MembershipType = MembershipType.External.ToString(),
                Status = WorkspaceMemberStatus.Active.ToString(),
                JoinedAt = now.AddDays(-3),
            },
            // Removed members must not inflate the count.
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = activeId,
                UserId = Guid.NewGuid(),
                RoleId = Guid.NewGuid(),
                MembershipType = MembershipType.Internal.ToString(),
                Status = WorkspaceMemberStatus.Removed.ToString(),
                JoinedAt = now.AddDays(-8),
                RemovedAt = now.AddDays(-4),
            });

        await db.SaveChangesAsync();

        MockAuthIdentity.GetUserByIdAsync(_ownerId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = _ownerId, FullName = "Mai Tran", Email = "mai@acme.com" });

        return (activeId, suspendedId);
    }

    [Fact]
    public async Task Directory_RejectsUnauthenticatedCallers()
    {
        var response = await Factory.CreateClient().GetAsync("/api/v1/admin/workspaces");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Directory_RejectsAuthenticatedNonAdmins()
    {
        var response = await MemberClient().GetAsync("/api/v1/admin/workspaces");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Directory_ReturnsWorkspacesTheAdminIsNotAMemberOf()
    {
        var (activeId, suspendedId) = await SeedAsync();

        var page = await AdminClient()
            .GetFromJsonAsync<AdminPagedResult<AdminWorkspaceSummaryDto>>("/api/v1/admin/workspaces");

        Assert.NotNull(page);
        Assert.Equal(2, page!.Total);
        var ids = page.Items.Select(item => item.Id).ToList();
        Assert.Contains(activeId, ids);
        Assert.Contains(suspendedId, ids);

        var active = page.Items.Single(item => item.Id == activeId);
        Assert.Equal(WorkspaceLifecycleStatus.Active, active.Status);
        Assert.Equal(2, active.MemberCount);
        Assert.Equal("Mai Tran", active.Owner.FullName);
        Assert.True(active.Owner.Resolved);

        Assert.Equal(
            WorkspaceLifecycleStatus.Suspended,
            page.Items.Single(item => item.Id == suspendedId).Status);
    }

    [Fact]
    public async Task Directory_FiltersBySearchStatusAndMemberCount()
    {
        var (activeId, suspendedId) = await SeedAsync();
        var client = AdminClient();

        var suspended = await client.GetFromJsonAsync<AdminPagedResult<AdminWorkspaceSummaryDto>>(
            "/api/v1/admin/workspaces?status=suspended");
        Assert.Equal(suspendedId, Assert.Single(suspended!.Items).Id);

        var searched = await client.GetFromJsonAsync<AdminPagedResult<AdminWorkspaceSummaryDto>>(
            "/api/v1/admin/workspaces?search=zenith");
        Assert.Equal(suspendedId, Assert.Single(searched!.Items).Id);

        var busy = await client.GetFromJsonAsync<AdminPagedResult<AdminWorkspaceSummaryDto>>(
            "/api/v1/admin/workspaces?minMembers=1");
        Assert.Equal(activeId, Assert.Single(busy!.Items).Id);

        var rejected = await client.GetAsync("/api/v1/admin/workspaces?status=trial");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Directory_PaginatesAndSortsServerSide()
    {
        await SeedAsync();
        var client = AdminClient();

        var firstPage = await client.GetFromJsonAsync<AdminPagedResult<AdminWorkspaceSummaryDto>>(
            "/api/v1/admin/workspaces?page=1&pageSize=1&sort=name_asc");
        var secondPage = await client.GetFromJsonAsync<AdminPagedResult<AdminWorkspaceSummaryDto>>(
            "/api/v1/admin/workspaces?page=2&pageSize=1&sort=name_asc");

        Assert.Equal(2, firstPage!.Total);
        Assert.Equal("Acme Localization", Assert.Single(firstPage.Items).Name);
        Assert.Equal("Zenith Media", Assert.Single(secondPage!.Items).Name);
    }

    [Fact]
    public async Task Detail_Returns404ForAnUnknownWorkspace()
    {
        await SeedAsync();

        var response = await AdminClient().GetAsync($"/api/v1/admin/workspaces/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_SuspendsReactivatesAndKeepsAnAppendOnlyTrail()
    {
        var (activeId, _) = await SeedAsync();
        var client = AdminClient();

        var suspend = await client.PostAsJsonAsync(
            $"/api/v1/admin/workspaces/{activeId}/suspend",
            new AdminWorkspaceLifecycleRequest("Abuse report from customer success"));
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);

        var suspended = await suspend.Content.ReadFromJsonAsync<AdminWorkspaceDetailDto>();
        Assert.Equal(WorkspaceLifecycleStatus.Suspended, suspended!.Status);
        Assert.NotNull(suspended.CurrentSuspension);
        Assert.Equal("Abuse report from customer success", suspended.CurrentSuspension!.Reason);
        Assert.Equal(_adminUserId, suspended.CurrentSuspension.PerformedBy);

        // Repeating an already-applied transition is a conflict, not a silent no-op.
        var repeat = await client.PostAsJsonAsync(
            $"/api/v1/admin/workspaces/{activeId}/suspend",
            new AdminWorkspaceLifecycleRequest("Abuse report from customer success"));
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);

        var noReason = await client.PostAsJsonAsync(
            $"/api/v1/admin/workspaces/{activeId}/reactivate",
            new AdminWorkspaceLifecycleRequest("   "));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var reactivate = await client.PostAsJsonAsync(
            $"/api/v1/admin/workspaces/{activeId}/reactivate",
            new AdminWorkspaceLifecycleRequest("Customer remediated the report"));
        Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);

        var reactivated = await reactivate.Content.ReadFromJsonAsync<AdminWorkspaceDetailDto>();
        Assert.Equal(WorkspaceLifecycleStatus.Active, reactivated!.Status);
        Assert.Null(reactivated.CurrentSuspension);
        Assert.Equal(2, reactivated.LifecycleHistory.Count);
        Assert.Equal(WorkspaceAdminActionTypes.Reactivate, reactivated.LifecycleHistory[0].Action);
        Assert.Equal(WorkspaceAdminActionTypes.Suspend, reactivated.LifecycleHistory[1].Action);
        Assert.Equal("Abuse report from customer success", reactivated.LifecycleHistory[1].Reason);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();

        // Lifecycle changes flip is_active only — no workspace data is removed.
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == activeId);
        Assert.True(workspace.IsActive);
        Assert.Null(workspace.DeletedAt);
        Assert.Equal("Acme Localization", workspace.Name);
        Assert.Equal(2, await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == activeId && m.RemovedAt == null));

        var trail = await db.WorkspaceAdminActions.AsNoTracking()
            .Where(action => action.WorkspaceId == activeId)
            .OrderBy(action => action.PerformedAt)
            .ToListAsync();
        Assert.Equal(2, trail.Count);
        Assert.All(trail, action => Assert.Equal(_adminUserId, action.PerformedBy));
    }

    [Fact]
    public async Task Lifecycle_RejectsNonAdmins()
    {
        var (activeId, _) = await SeedAsync();

        var response = await MemberClient().PostAsJsonAsync(
            $"/api/v1/admin/workspaces/{activeId}/suspend",
            new AdminWorkspaceLifecycleRequest("Trying to suspend a workspace I do not own"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
