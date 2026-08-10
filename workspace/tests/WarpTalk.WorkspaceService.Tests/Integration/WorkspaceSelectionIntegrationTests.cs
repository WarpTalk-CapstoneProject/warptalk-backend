using System;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Models;
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

    [Fact]
    public async Task GetWorkspaceById_WhenWorkspaceIsInactive_ReturnsNotFound()
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
            Status = WorkspaceMemberStatus.Active.ToStorageValue(),
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        AuthorizeClient(userId, "owner@company.com");

        var response = await Client.GetAsync($"/api/v1/workspaces/{workspaceId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetWorkspaceById_WhenWorkspaceIsSoftDeleted_ReturnsNotFound()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Deleted Workspace",
            Slug = "deleted-workspace",
            OwnerId = userId,
            IsActive = true,
            DeletedAt = DateTime.UtcNow,
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
            Status = WorkspaceMemberStatus.Active.ToStorageValue(),
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        AuthorizeClient(userId, "owner@company.com");

        var response = await Client.GetAsync($"/api/v1/workspaces/{workspaceId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SelectWorkspace_ReturnsRoleAndMembershipTypeFromPersistedMembership()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        MockAuthIdentity
            .GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "External Workspace",
            Slug = "external-workspace",
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
            RoleId = roleId,
            MembershipType = MembershipType.External.ToString(),
            Status = WorkspaceMemberStatus.Active.ToStorageValue(),
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        AuthorizeClient(userId, "member@verified-company.com");

        var response = await Client.PostAsync($"/api/v1/workspaces/{workspaceId}/select", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<SelectWorkspaceResponse>();
        Assert.NotNull(payload);
        Assert.Equal("Member", payload!.Role);
        Assert.Equal("External", payload.MembershipType);
    }

    [Fact]
    public async Task GetWorkspaces_ExcludesInactiveDeletedAndSuspendedMembershipRows()
    {
        var userId = Guid.NewGuid();
        var activeRoleId = Guid.NewGuid();
        var suspendedRoleId = Guid.NewGuid();

        MockAuthIdentity
            .GetRoleByIdAsync(activeRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = activeRoleId, Name = "Member" });
        MockAuthIdentity
            .GetRoleByIdAsync(suspendedRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = suspendedRoleId, Name = "Member" });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();

        var activeWorkspaceId = Guid.NewGuid();
        var deletedWorkspaceId = Guid.NewGuid();
        var inactiveWorkspaceId = Guid.NewGuid();
        var suspendedMembershipWorkspaceId = Guid.NewGuid();

        db.Workspaces.AddRange(
            new Workspace
            {
                Id = activeWorkspaceId,
                Name = "Active Workspace",
                Slug = "active-workspace",
                OwnerId = Guid.NewGuid(),
                IsActive = true,
                Settings = "{}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Workspace
            {
                Id = deletedWorkspaceId,
                Name = "Deleted Workspace",
                Slug = "deleted-workspace",
                OwnerId = Guid.NewGuid(),
                IsActive = true,
                DeletedAt = DateTime.UtcNow,
                Settings = "{}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Workspace
            {
                Id = inactiveWorkspaceId,
                Name = "Inactive Workspace",
                Slug = "inactive-workspace",
                OwnerId = Guid.NewGuid(),
                IsActive = false,
                Settings = "{}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Workspace
            {
                Id = suspendedMembershipWorkspaceId,
                Name = "Suspended Membership Workspace",
                Slug = "suspended-membership-workspace",
                OwnerId = Guid.NewGuid(),
                IsActive = true,
                Settings = "{}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

        db.WorkspaceMembers.AddRange(
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = activeWorkspaceId,
                UserId = userId,
                RoleId = activeRoleId,
                MembershipType = MembershipType.External.ToString(),
                Status = WorkspaceMemberStatus.Active.ToStorageValue(),
                JoinedAt = DateTime.UtcNow
            },
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = deletedWorkspaceId,
                UserId = userId,
                RoleId = activeRoleId,
                MembershipType = MembershipType.External.ToString(),
                Status = WorkspaceMemberStatus.Active.ToStorageValue(),
                JoinedAt = DateTime.UtcNow
            },
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = inactiveWorkspaceId,
                UserId = userId,
                RoleId = activeRoleId,
                MembershipType = MembershipType.External.ToString(),
                Status = WorkspaceMemberStatus.Active.ToStorageValue(),
                JoinedAt = DateTime.UtcNow
            },
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = suspendedMembershipWorkspaceId,
                UserId = userId,
                RoleId = suspendedRoleId,
                MembershipType = MembershipType.External.ToString(),
                Status = WorkspaceMemberStatus.Suspended.ToStorageValue(),
                JoinedAt = DateTime.UtcNow
            });

        await db.SaveChangesAsync();

        AuthorizeClient(userId, "member@company.com");

        var response = await Client.GetFromJsonAsync<PagedResult<WorkspaceDto>>("/api/v1/workspaces?page=1&pageSize=10");

        Assert.NotNull(response);
        Assert.Single(response!.Items);
        Assert.Equal(activeWorkspaceId, response.Items[0].Id);
        Assert.Equal("External", response.Items[0].MembershipType);
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
