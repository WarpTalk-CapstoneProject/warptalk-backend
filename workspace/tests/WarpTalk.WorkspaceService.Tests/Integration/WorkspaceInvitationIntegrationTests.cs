using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
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
    public async Task AcceptInvitation_SubdomainUser_WhenSubdomainsAllowed_Succeeds()
    {
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var inviteEmail = "employee@eng.company.com";
        var rawToken = "subdomain-accept-token";
        var tokenHash = TokenHasher.Hash(rawToken);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var workspace = new Workspace
            {
                Id = workspaceId,
                Name = "Company Workspace",
                Slug = "company-workspace",
                OwnerId = Guid.NewGuid(),
                AllowExternalCollaboration = true,
                RequireVerifiedDomainForInternal = true,
                AllowSubdomains = true,
                Settings = "{\"VerifiedDomains\":[\"company.com\"],\"AllowExternalCollaboration\":true,\"AllowSubdomains\":true}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Workspaces.Add(workspace);

            db.WorkspaceVerifiedDomains.Add(new WorkspaceVerifiedDomain
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
            });

            db.WorkspaceInvitations.Add(new WorkspaceInvitation
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
            });

            await db.SaveChangesAsync();
        }

        MockAuthIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GenerateJwtToken(userId, inviteEmail));

        var response = await Client.PostAsJsonAsync(
            "/api/v1/workspaces/invitations/accept",
            new AcceptInvitationRequest(rawToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var invitation = await db.WorkspaceInvitations.FirstOrDefaultAsync(x => x.TokenHash == tokenHash);
            Assert.NotNull(invitation);
            Assert.Equal(InvitationStatus.ACCEPTED.ToString(), invitation!.Status);

            var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId);
            Assert.NotNull(member);
            Assert.Equal(MembershipType.Internal.ToString(), member!.MembershipType);
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
                // The one-Enterprise-home-per-user rule keys off this column. It used to key off
                // the VerifiedDomains list in the settings JSON as well, which is what made a
                // workspace that had switched the policy off still behave as if it were on.
                RequireVerifiedDomainForInternal = true,
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
                RequireVerifiedDomainForInternal = true,
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
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AcceptInvitation_WhenVerifiedDomainWasRemoved_ReturnsBadRequestAndLeavesInvitationPending()
    {
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var inviteEmail = "employee@company.com";
        var rawToken = "policy-conflict-token";
        var tokenHash = TokenHasher.Hash(rawToken);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Company Workspace",
                Slug = "company-workspace",
                OwnerId = Guid.NewGuid(),
                AllowExternalCollaboration = true,
                RequireVerifiedDomainForInternal = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

            db.WorkspaceInvitations.Add(new WorkspaceInvitation
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
            });

            await db.SaveChangesAsync();
        }

        MockAuthIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Admin" });

        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GenerateJwtToken(userId, inviteEmail));

        var response = await Client.PostAsJsonAsync(
            "/api/v1/workspaces/invitations/accept",
            new AcceptInvitationRequest(rawToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.NotNull(error);
        Assert.Contains(WorkspaceConstants.Errors.CannotInviteInternalWithoutVerifiedDomain, error!.Error);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var invitation = await db.WorkspaceInvitations.FirstOrDefaultAsync(x => x.TokenHash == tokenHash);
            Assert.NotNull(invitation);
            Assert.Equal(InvitationStatus.PENDING.ToString(), invitation!.Status);

            var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId);
            Assert.Null(member);
        }
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

    [Fact]
    public async Task GetInvitationPolicy_PublicEmailForOwner_ReturnsSuggestedAccessAndDisabledReason()
    {
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Company Workspace",
                Slug = "company-workspace",
                OwnerId = ownerUserId,
                AllowExternalCollaboration = true,
                RequireVerifiedDomainForInternal = true,
                AllowSubdomains = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = ownerUserId,
                RoleId = ownerRoleId,
                MembershipType = MembershipType.Internal.ToString(),
                Status = "Active",
                JoinedAt = DateTime.UtcNow
            });

            db.WorkspaceVerifiedDomains.Add(new WorkspaceVerifiedDomain
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Domain = "company.com",
                Status = "verified",
                VerificationMethod = "system",
                VerificationToken = Guid.NewGuid().ToString(),
                VerifiedAt = DateTime.UtcNow,
                VerifiedBy = ownerUserId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = ownerUserId,
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = ownerUserId
            });

            await db.SaveChangesAsync();
        }

        MockAuthIdentity.GetRoleByIdAsync(ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = ownerRoleId, Name = "Owner" });

        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GenerateJwtToken(ownerUserId, "owner@company.com"));

        var response = await Client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/invitations/policy?email=someone@gmail.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<InvitationPolicyResponse>();
        Assert.NotNull(content);
        Assert.Equal(MembershipType.External.ToString(), content!.SuggestedMembershipType);
        Assert.DoesNotContain(MembershipType.Internal.ToString(), content.AllowedMembershipTypes);
        Assert.Contains(MembershipType.External.ToString(), content.AllowedMembershipTypes);
        Assert.True(content.RequireVerifiedDomainForInternal);
        Assert.True(content.AllowExternalCollaboration);
        Assert.True(content.AllowSubdomains);
        Assert.False(content.IsEmailDomainVerified);
        Assert.True(content.IsPublicEmailDomain);
        Assert.Equal(WorkspaceConstants.Errors.CannotInviteInternalWithPublicDomain, content.InternalDisabledReason);
        Assert.Null(content.ExternalDisabledReason);
    }
}
