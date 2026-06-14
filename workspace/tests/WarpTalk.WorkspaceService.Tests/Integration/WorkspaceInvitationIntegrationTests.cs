using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Integration;

public class WorkspaceInvitationIntegrationTests : BaseIntegrationTest
{
    [Fact]
    public async Task PreviewInvitation_ValidToken_ReturnsPreviewAndAccountExists()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var inviteEmail = "new_employee@company.com";
        var rawToken = "mysecrettoken123";
        var tokenHash = TokenHasher.Hash(rawToken);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var ws = new Workspace
            {
                Id = workspaceId,
                Name = "Company Workspace",
                Slug = "company-workspace",
                OwnerId = Guid.NewGuid(),
                AllowExternalCollaboration = true,
                Settings = "{\"VerifiedDomains\":[\"company.com\"],\"AllowExternalCollaboration\":true}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Workspaces.Add(ws);

            var invitation = new WorkspaceInvitation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Email = inviteEmail,
                RoleId = roleId,
                MembershipType = MembershipType.Internal.ToString(),
                InvitedBy = Guid.NewGuid(),
                TokenHash = tokenHash,
                Status = InvitationStatus.PENDING.ToString(),
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow
            };
            db.WorkspaceInvitations.Add(invitation);
            await db.SaveChangesAsync();
        }

        // Mock AuthIdentity to return null (meaning user has NOT registered)
        MockAuthIdentity.GetUserByEmailAsync(inviteEmail, Arg.Any<CancellationToken>())
            .Returns((User?)null);
        MockAuthIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        // Act
        var response = await Client.GetAsync($"/api/v1/workspaces/invitations/preview?token={rawToken}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<PreviewInvitationResponse>();
        Assert.NotNull(content);
        Assert.Equal("Company Workspace", content.WorkspaceName);
        Assert.Equal("Member", content.RoleName);
        Assert.False(content.AccountExists);
    }

    [Fact]
    public async Task AcceptInvitation_ValidUser_Succeeds()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var inviteEmail = "employee@company.com";
        var rawToken = "accepttoken123";
        var tokenHash = TokenHasher.Hash(rawToken);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var ws = new Workspace
            {
                Id = workspaceId,
                Name = "Company Workspace",
                Slug = "company-workspace",
                OwnerId = Guid.NewGuid(),
                AllowExternalCollaboration = true,
                Settings = "{\"VerifiedDomains\":[\"company.com\"],\"AllowExternalCollaboration\":true}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Workspaces.Add(ws);

            var verifiedDomain = new WorkspaceVerifiedDomain
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Domain = "company.com",
                Status = "verified",
                VerificationMethod = "system",
                VerificationToken = Guid.NewGuid().ToString(),
                VerifiedAt = DateTime.UtcNow,
                VerifiedBy = userId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = userId,
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = userId
            };
            db.WorkspaceVerifiedDomains.Add(verifiedDomain);

            var invitation = new WorkspaceInvitation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Email = inviteEmail,
                RoleId = roleId,
                MembershipType = MembershipType.Internal.ToString(),
                InvitedBy = Guid.NewGuid(),
                TokenHash = tokenHash,
                Status = InvitationStatus.PENDING.ToString(),
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow
            };
            db.WorkspaceInvitations.Add(invitation);
            await db.SaveChangesAsync();
        }

        MockAuthIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        var jwtToken = GenerateJwtToken(userId, inviteEmail);
        Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);

        // Act
        var response = await Client.PostAsJsonAsync("/api/v1/workspaces/invitations/accept", new AcceptInvitationRequest(rawToken));

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var dbInviteFound = await db.WorkspaceInvitations.FirstOrDefaultAsync(x => x.TokenHash == tokenHash);
            Assert.NotNull(dbInviteFound);
            Assert.Equal(InvitationStatus.ACCEPTED.ToString(), dbInviteFound.Status);

            var dbMember = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId);
            Assert.NotNull(dbMember);
            Assert.Equal(MembershipType.Internal.ToString(), dbMember.MembershipType);
        }
    }

    [Fact]
    public async Task AcceptInvitation_UserAlreadyInternalInAnotherEnterprise_ReturnsForbidden()
    {
        // Arrange
        var workspaceId1 = Guid.NewGuid();
        var workspaceId2 = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var inviteEmail = "employee@company.com";
        var rawToken = "accepttoken456";
        var tokenHash = TokenHasher.Hash(rawToken);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            
            // Workspace 1 (Target workspace, which user is invited to)
            var ws1 = new Workspace
            {
                Id = workspaceId1,
                Name = "Company Workspace 1",
                Slug = "company-workspace-1",
                OwnerId = Guid.NewGuid(),
                AllowExternalCollaboration = true,
                Settings = "{\"VerifiedDomains\":[\"company.com\"],\"AllowExternalCollaboration\":true}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Workspaces.Add(ws1);

            var verifiedDomain = new WorkspaceVerifiedDomain
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId1,
                Domain = "company.com",
                Status = "verified",
                VerificationMethod = "system",
                VerificationToken = Guid.NewGuid().ToString(),
                VerifiedAt = DateTime.UtcNow,
                VerifiedBy = userId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = userId,
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = userId
            };
            db.WorkspaceVerifiedDomains.Add(verifiedDomain);

            // Workspace 2 (Workspace user already belongs to as an Internal member)
            var ws2 = new Workspace
            {
                Id = workspaceId2,
                Name = "Company Workspace 2",
                Slug = "company-workspace-2",
                OwnerId = Guid.NewGuid(),
                AllowExternalCollaboration = true,
                Settings = "{\"VerifiedDomains\":[\"company.com\"],\"AllowExternalCollaboration\":true}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Workspaces.Add(ws2);

            var invitation = new WorkspaceInvitation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId1,
                Email = inviteEmail,
                RoleId = roleId,
                MembershipType = MembershipType.Internal.ToString(),
                InvitedBy = Guid.NewGuid(),
                TokenHash = tokenHash,
                Status = InvitationStatus.PENDING.ToString(),
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow
            };
            db.WorkspaceInvitations.Add(invitation);

            // Existing membership in WS 2
            var existingMember = new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId2,
                UserId = userId,
                RoleId = roleId,
                MembershipType = MembershipType.Internal.ToString(),
                Status = "Active",
                JoinedAt = DateTime.UtcNow
            };
            db.WorkspaceMembers.Add(existingMember);

            await db.SaveChangesAsync();
        }

        MockAuthIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        var jwtToken = GenerateJwtToken(userId, inviteEmail);
        Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);

        // Act
        var response = await Client.PostAsJsonAsync("/api/v1/workspaces/invitations/accept", new AcceptInvitationRequest(rawToken));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AcceptInvitation_WorkspaceWithoutVerifiedDomains_Succeeds()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var inviteEmail = "anyone@gmail.com";
        var rawToken = "personaltoken123";
        var tokenHash = TokenHasher.Hash(rawToken);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var ws = new Workspace
            {
                Id = workspaceId,
                Name = "Personal Workspace",
                Slug = "personal-workspace",
                OwnerId = Guid.NewGuid(),
                AllowExternalCollaboration = true,
                Settings = "{\"VerifiedDomains\":[],\"RequireVerifiedDomainForInternal\":false}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Workspaces.Add(ws);

            var invitation = new WorkspaceInvitation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Email = inviteEmail,
                RoleId = roleId,
                MembershipType = MembershipType.Internal.ToString(),
                InvitedBy = Guid.NewGuid(),
                TokenHash = tokenHash,
                Status = InvitationStatus.PENDING.ToString(),
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow
            };
            db.WorkspaceInvitations.Add(invitation);
            await db.SaveChangesAsync();
        }

        MockAuthIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        var jwtToken = GenerateJwtToken(userId, inviteEmail);
        Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);

        // Act
        var response = await Client.PostAsJsonAsync("/api/v1/workspaces/invitations/accept", new AcceptInvitationRequest(rawToken));

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var dbInviteFound = await db.WorkspaceInvitations.FirstOrDefaultAsync(x => x.TokenHash == tokenHash);
            Assert.NotNull(dbInviteFound);
            Assert.Equal(InvitationStatus.ACCEPTED.ToString(), dbInviteFound.Status);

            var dbMember = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId);
            Assert.NotNull(dbMember);
            Assert.Equal(MembershipType.Internal.ToString(), dbMember.MembershipType);
        }
    }
}
