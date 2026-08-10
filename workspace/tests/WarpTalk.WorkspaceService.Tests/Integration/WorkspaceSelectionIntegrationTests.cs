using System;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Integration;

public class WorkspaceSelectionIntegrationTests : BaseIntegrationTest
{
    [Fact]
    public async Task GetWorkspaceById_WhenUserIsNotMember_ReturnsNotFound()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await SeedWorkspaceAsync(new Workspace
        {
            Id = workspaceId,
            Name = "Hidden Workspace",
            Slug = "hidden-workspace",
            OwnerId = Guid.NewGuid(),
            IsActive = true,
            Settings = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        AuthorizeClient(userId, "user@company.com");

        var response = await Client.GetAsync($"/api/v1/workspaces/{workspaceId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SelectWorkspace_WhenUserIsNotMember_ReturnsNotFound()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await SeedWorkspaceAsync(new Workspace
        {
            Id = workspaceId,
            Name = "Hidden Workspace",
            Slug = "hidden-workspace",
            OwnerId = Guid.NewGuid(),
            IsActive = true,
            Settings = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        AuthorizeClient(userId, "user@company.com");

        var response = await Client.PostAsync($"/api/v1/workspaces/{workspaceId}/select", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SelectWorkspace_WhenWorkspaceIsInactive_ReturnsNotFound()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();

        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Inactive Workspace",
            Slug = "inactive-workspace",
            OwnerId = userId,
            IsActive = false,
            Settings = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            MembershipType = MembershipType.Internal.ToString(),
            Status = WorkspaceMemberStatus.Active.ToString(),
            JoinedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        AuthorizeClient(userId, "owner@company.com");

        var response = await Client.PostAsync($"/api/v1/workspaces/{workspaceId}/select", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SelectWorkspace_WhenMembershipIsSuspended_ReturnsNotFound()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Suspended Membership Workspace",
            Slug = "suspended-membership-workspace",
            OwnerId = Guid.NewGuid(),
            IsActive = true,
            Settings = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = Guid.NewGuid(),
            MembershipType = MembershipType.Internal.ToString(),
            Status = WorkspaceMemberStatus.Suspended.ToStorageValue(),
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        AuthorizeClient(userId, "suspended@company.com");

        var response = await Client.PostAsync($"/api/v1/workspaces/{workspaceId}/select", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task SeedWorkspaceAsync(Workspace workspace)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
    }

    private void AuthorizeClient(Guid userId, string email)
    {
        var jwtToken = GenerateJwtToken(userId, email);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
    }
}
