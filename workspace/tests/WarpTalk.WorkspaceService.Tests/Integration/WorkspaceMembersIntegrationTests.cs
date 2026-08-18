using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Integration;

public class WorkspaceMembersIntegrationTests : BaseIntegrationTest
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _ownerUserId = Guid.NewGuid();
    private readonly Guid _activeMemberUserId = Guid.NewGuid();
    private readonly Guid _suspendedMemberUserId = Guid.NewGuid();
    private readonly Guid _removedMemberUserId = Guid.NewGuid();

    private readonly Guid _ownerRoleId = Guid.NewGuid();
    private readonly Guid _memberRoleId = Guid.NewGuid();

    private HttpClient OwnerClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            GenerateJwtToken(_ownerUserId, "owner@acme.com"));
        return client;
    }

    private HttpClient MemberClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            GenerateJwtToken(_activeMemberUserId, "active@acme.com"));
        return client;
    }

    private async Task SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var now = DateTime.UtcNow;

        db.Workspaces.Add(new Workspace
        {
            Id = _workspaceId,
            Name = "Acme Localization",
            Slug = "acme-localization",
            OwnerId = _ownerUserId,
            Settings = "{}",
            IsActive = true,
            CreatedAt = now.AddDays(-10),
            UpdatedAt = now.AddDays(-1)
        });

        db.WorkspaceMembers.AddRange(
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = _workspaceId,
                UserId = _ownerUserId,
                RoleId = _ownerRoleId,
                MembershipType = MembershipType.Internal.ToString(),
                Status = WorkspaceMemberStatus.Active.ToStorageValue(),
                JoinedAt = now.AddDays(-9),
                CanCreateMeetings = true
            },
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = _workspaceId,
                UserId = _activeMemberUserId,
                RoleId = _memberRoleId,
                MembershipType = MembershipType.Internal.ToString(),
                Status = WorkspaceMemberStatus.Active.ToStorageValue(),
                JoinedAt = now.AddDays(-5),
                CanCreateMeetings = true
            },
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = _workspaceId,
                UserId = _suspendedMemberUserId,
                RoleId = _memberRoleId,
                MembershipType = MembershipType.Internal.ToString(),
                Status = WorkspaceMemberStatus.Suspended.ToStorageValue(),
                JoinedAt = now.AddDays(-3),
                CanCreateMeetings = false
            },
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = _workspaceId,
                UserId = _removedMemberUserId,
                RoleId = _memberRoleId,
                MembershipType = MembershipType.Internal.ToString(),
                Status = WorkspaceMemberStatus.Removed.ToStorageValue(),
                JoinedAt = now.AddDays(-1),
                RemovedAt = now.AddHours(-12),
                RemovedBy = _ownerUserId,
                CanCreateMeetings = false
            });

        await db.SaveChangesAsync();

        MockAuthIdentity.GetRoleByIdAsync(_ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _ownerRoleId, Name = "Owner" });
        MockAuthIdentity.GetRoleByIdAsync(_memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _memberRoleId, Name = "Member" });

        MockAuthIdentity.GetUserByIdAsync(_ownerUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = _ownerUserId, FullName = "Olivia Owner", Email = "owner@acme.com" });
        MockAuthIdentity.GetUserByIdAsync(_activeMemberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = _activeMemberUserId, FullName = "Alicia Active", Email = "active@acme.com" });
        MockAuthIdentity.GetUserByIdAsync(_suspendedMemberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = _suspendedMemberUserId, FullName = "Sam Suspended", Email = "suspended@acme.com" });
        MockAuthIdentity.GetUserByIdAsync(_removedMemberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = _removedMemberUserId, FullName = "Riley Removed", Email = "removed@acme.com" });
    }

    [Fact]
    public async Task ListMembers_ReturnsActiveAndSuspendedMembersButNotRemoved_ForOwnerDirectoryView()
    {
        await SeedAsync();

        var firstPage = await OwnerClient().GetFromJsonAsync<PagedResult<WorkspaceMemberDto>>(
            $"/api/v1/workspaces/{_workspaceId}/members?page=1&pageSize=2");
        var secondPage = await OwnerClient().GetFromJsonAsync<PagedResult<WorkspaceMemberDto>>(
            $"/api/v1/workspaces/{_workspaceId}/members?page=2&pageSize=2");

        Assert.NotNull(firstPage);
        Assert.NotNull(secondPage);
        Assert.Equal(3, firstPage!.Total);
        Assert.Equal(3, secondPage!.Total);
        Assert.Equal(
            new[] { "Sam Suspended", "Alicia Active" },
            firstPage.Items.Select(item => item.FullName).ToArray());
        Assert.Equal("Olivia Owner", Assert.Single(secondPage.Items).FullName);
        Assert.DoesNotContain(firstPage.Items.Concat(secondPage.Items), item => item.FullName == "Riley Removed");
    }

    [Fact]
    public async Task ListMembers_ReturnsOnlyActiveNonRemovedMembers_ForRegularMemberDirectoryView()
    {
        await SeedAsync();

        var page = await MemberClient().GetFromJsonAsync<PagedResult<WorkspaceMemberDto>>(
            $"/api/v1/workspaces/{_workspaceId}/members?page=1&pageSize=10");

        Assert.NotNull(page);
        Assert.Equal(2, page!.Total);
        Assert.Equal(
            new[] { "Alicia Active", "Olivia Owner" },
            page.Items.Select(item => item.FullName).ToArray());
    }

    [Fact]
    public async Task ListMembers_SearchDoesNotResurfaceRemovedMembers_ForOwnerDirectoryView()
    {
        await SeedAsync();

        var page = await OwnerClient().GetFromJsonAsync<PagedResult<WorkspaceMemberDto>>(
            $"/api/v1/workspaces/{_workspaceId}/members?page=1&pageSize=10&search=removed");

        Assert.NotNull(page);
        Assert.Empty(page!.Items);
        Assert.Equal(0, page.Total);

        var suspendedPage = await OwnerClient().GetFromJsonAsync<PagedResult<WorkspaceMemberDto>>(
            $"/api/v1/workspaces/{_workspaceId}/members?page=1&pageSize=10&search=suspended");

        Assert.NotNull(suspendedPage);
        Assert.Equal("Sam Suspended", Assert.Single(suspendedPage!.Items).FullName);
        Assert.Equal(1, suspendedPage.Total);
    }
}
