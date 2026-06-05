using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.AuthService.Tests.Integration;

public class AuthControllerIntegrationTests : BaseIntegrationTest
{
    [Fact]
    public async Task RegisterInvited_ValidRequest_RegistersUserAndAcceptsInvitation()
    {
        // Arrange
        var token = "validtoken_abc";
        var wsId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var email = "invited_member@company.com";

        MockWorkspaceInvitationClient.VerifyInvitationTokenAsync(token, Arg.Any<CancellationToken>())
            .Returns(new VerifyInvitationResult(true, email, wsId, "Company Workspace", roleId, "Member", "Internal", null));

        MockWorkspaceInvitationClient.AcceptInvitationAsync(token, Arg.Any<Guid>(), email, Arg.Any<CancellationToken>())
            .Returns(new AcceptInvitationResult(true, null));

        var request = new RegisterInvitedRequest(token, "SecurePassword123", "John Doe");

        // Act
        var response = await Client.PostAsJsonAsync("/api/v1/auth/register-invited", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(authResponse);
        Assert.Equal(email, authResponse.User.Email);

        // Verify database state
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var dbUser = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            Assert.NotNull(dbUser);
            Assert.True(dbUser.EmailVerified);
            Assert.Equal("John Doe", dbUser.FullName);
        }
    }

    [Fact]
    public async Task RegisterInvited_EmailAlreadyExists_ReturnsBadRequest()
    {
        // Arrange
        var token = "validtoken_exists";
        var email = "existing_user@company.com";
        var wsId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        // Seed user
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var userId = Guid.NewGuid();
            var settings = new UserSetting
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Theme = "system",
                AutoGenerateSummary = true,
                MicNoiseSuppression = true,
                ShowOriginalTranscript = true,
                ShowTranslatedTranscript = true,
                TranscriptFontSize = 14,
                DefaultSpeakLanguage = "vi-VN",
                DefaultListenLanguage = "en-US",
                DefaultTranslationRoomType = "group",
                DefaultMaxParticipants = 10,
                UpdatedAt = DateTime.UtcNow
            };
            var user = new User
            {
                Id = userId,
                Email = email,
                FullName = "Existing Person",
                PasswordHash = "hash",
                PreferredLanguage = "en",
                Timezone = "UTC",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserSettings.Add(settings);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        MockWorkspaceInvitationClient.VerifyInvitationTokenAsync(token, Arg.Any<CancellationToken>())
            .Returns(new VerifyInvitationResult(true, email, wsId, "Company Workspace", roleId, "Member", "Internal", null));

        var request = new RegisterInvitedRequest(token, "SecurePassword123", "New Name");

        // Act
        var response = await Client.PostAsJsonAsync("/api/v1/auth/register-invited", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterInvited_WorkspaceServiceAcceptFails_RollsBackUserAndReturnsBadRequest()
    {
        // Arrange
        var token = "validtoken_rollback";
        var wsId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var email = "rollback_member@company.com";

        MockWorkspaceInvitationClient.VerifyInvitationTokenAsync(token, Arg.Any<CancellationToken>())
            .Returns(new VerifyInvitationResult(true, email, wsId, "Company Workspace", roleId, "Member", "Internal", null));

        // Workspace service returns error (e.g. user already belongs to another Enterprise workspace)
        MockWorkspaceInvitationClient.AcceptInvitationAsync(token, Arg.Any<Guid>(), email, Arg.Any<CancellationToken>())
            .Returns(new AcceptInvitationResult(false, "User already belongs to another Enterprise Workspace."));

        var request = new RegisterInvitedRequest(token, "SecurePassword123", "Rollback User");

        // Act
        var response = await Client.PostAsJsonAsync("/api/v1/auth/register-invited", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify that user was NOT created in DB (Transaction rolled back)
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var dbUser = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            Assert.Null(dbUser);
        }
    }
}
