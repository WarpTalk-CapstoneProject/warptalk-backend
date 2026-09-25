using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AuthService.API.Controllers;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Infrastructure.Security;
using WarpTalk.Shared;
using WarpTalk.Shared.PlatformSettings;
using Xunit;
using AuthServiceImpl = WarpTalk.AuthService.Application.Services.AuthService;
using GoogleAuthServiceImpl = WarpTalk.AuthService.Application.Services.GoogleAuthService;

namespace WarpTalk.AuthService.Tests.Settings;

/// <summary>
/// Every Security &amp; auth setting the auth service owns is read LIVE by the code that enforces it:
/// set a value, drive the real path, change it, drive it again — no restart, no re-construction of
/// the singleton that reads it.
/// </summary>
public sealed class AuthPlatformSettingsReadersTests
{
    private readonly InMemoryPlatformSettingsSource _source = new();
    private readonly PlatformSettingsReader _reader;

    public AuthPlatformSettingsReadersTests()
    {
        _reader = new PlatformSettingsReader(_source, NullLogger<PlatformSettingsReader>.Instance, cacheTtl: TimeSpan.FromMinutes(5));
    }

    private async Task Publish(Action<InMemoryPlatformSettingsSource> change)
    {
        change(_source);
        await _reader.RefreshAsync();
    }

    [Fact]
    public async Task Token_lifetimes_follow_the_setting_with_the_jwt_section_as_fallback()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-that-is-at-least-thirty-two-chars",
            ["Jwt:AccessTokenExpiryMinutes"] = "45",
        }).Build();
        var generator = new JwtTokenGenerator(configuration, _reader);

        Assert.Equal(45, generator.AccessTokenExpiryMinutes);
        Assert.Equal(7, generator.RefreshTokenExpiryDays);

        await Publish(s => s.Set(PlatformSettingsCatalog.AccessTokenMinutes, 15).Set(PlatformSettingsCatalog.RefreshTokenDays, 30));
        Assert.Equal(15, generator.AccessTokenExpiryMinutes);
        Assert.Equal(30, generator.RefreshTokenExpiryDays);

        // The token actually issued carries the live lifetime.
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(
            generator.GenerateAccessToken(Guid.NewGuid(), "a@b.co", true, ["user"]));
        Assert.InRange((token.ValidTo - DateTime.UtcNow).TotalMinutes, 14, 15.1);
    }

    [Fact]
    public async Task Session_cookies_live_as_long_as_the_refresh_token()
    {
        var generator = new JwtTokenGenerator(new ConfigurationBuilder().Build(), _reader);
        await Publish(s => s.Set(PlatformSettingsCatalog.RefreshTokenDays, 30));

        var authService = Substitute.For<IAuthService>();
        authService.LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AuthResponse("access", "refresh", DateTime.UtcNow.AddMinutes(30),
                new UserDto(Guid.NewGuid(), "a@b.co", "A", null, null, "vi-VN", "UTC", true, AccountStatus.ACTIVE, ["user"]))));
        var controller = new AuthController(authService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddSingleton<IJwtTokenGenerator>(generator).BuildServiceProvider(),
                },
            },
        };

        await controller.Login(new LoginRequest("a@b.co", "Password123!", null, null), CancellationToken.None);

        var refreshCookie = controller.Response.Headers.SetCookie.Single(c => c!.StartsWith("warptalk_refresh=", StringComparison.Ordinal))!;
        var expires = DateTimeOffset.Parse(
            Regex.Match(refreshCookie, "expires=([^;]+)", RegexOptions.IgnoreCase).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange((expires - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
    }

    [Fact]
    public async Task Password_floor_follows_the_setting_in_every_password_validator()
    {
        Assert.True((await new RegisterRequestValidator(_reader).ValidateAsync(new RegisterRequest("a@b.co", "123456", "A"))).IsValid);

        await Publish(s => s.Set(PlatformSettingsCatalog.PasswordMinLength, 10));

        var register = await new RegisterRequestValidator(_reader).ValidateAsync(new RegisterRequest("a@b.co", "123456789", "A"));
        Assert.False(register.IsValid);
        Assert.Contains(register.Errors, e => e.ErrorMessage.Contains("10 characters", StringComparison.Ordinal));
        Assert.True((await new RegisterRequestValidator(_reader).ValidateAsync(new RegisterRequest("a@b.co", "1234567890", "A"))).IsValid);

        Assert.Equal(10, PasswordPolicy.MinLength(_reader));
        Assert.False((await new RegisterInvitedRequestValidator(_reader).ValidateAsync(new RegisterInvitedRequest("token", "123456789", "A"))).IsValid);
        Assert.True((await new RegisterInvitedRequestValidator(_reader).ValidateAsync(new RegisterInvitedRequest("token", "1234567890", "A"))).IsValid);
        Assert.Contains(
            (await new ChangePasswordRequestValidator(_reader).ValidateAsync(new ChangePasswordRequest("old-password", "123456789"))).Errors,
            e => e.ErrorMessage.Contains("10 characters", StringComparison.Ordinal));
        Assert.Contains(
            (await new ResetPasswordRequestValidator(_reader).ValidateAsync(new ResetPasswordRequest("token", "123456789"))).Errors,
            e => e.ErrorMessage.Contains("10 characters", StringComparison.Ordinal));

        // Without settings (a test host, or no Redis) the historic floor stands.
        Assert.Equal(6, PasswordPolicy.MinLength(null));
    }

    [Fact]
    public async Task Lockout_uses_the_live_attempt_count_and_duration()
    {
        var (service, users, hasher) = AuthService();
        var user = new User { Id = Guid.NewGuid(), Email = "a@b.co", PasswordHash = "hash", IsActive = true, EmailVerified = true, FailedLoginAttempts = 2 };
        users.GetByEmailWithRolesAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        hasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        // Default policy is 5 attempts: a third failure does not lock.
        await service.LoginAsync(new LoginRequest(user.Email, "wrong", null, null));
        Assert.False(user.IsLocked);

        await Publish(s => s.Set(PlatformSettingsCatalog.LockoutMaxFailedAttempts, 4).Set(PlatformSettingsCatalog.LockoutDurationMinutes, 90));
        await service.LoginAsync(new LoginRequest(user.Email, "wrong", null, null));

        Assert.True(user.IsLocked);
        Assert.InRange((user.LockedUntil!.Value - DateTime.UtcNow).TotalMinutes, 89, 90.1);
    }

    [Fact]
    public async Task Google_sign_in_is_refused_while_switched_off()
    {
        var verifier = Substitute.For<IGoogleTokenVerifier>();
        var service = new GoogleAuthServiceImpl(
            UnitOfWork(out _), Substitute.For<IJwtTokenGenerator>(), verifier, Substitute.For<IDistributedCache>(),
            Options.Create(new AuthSettings()), NullLogger<GoogleAuthServiceImpl>.Instance, Substitute.For<IAuthEmailSender>(),
            platformSettings: _reader);

        await Publish(s => s.Set(PlatformSettingsCatalog.GoogleSignInEnabled, false));
        var login = await service.GoogleLoginAsync(new GoogleLoginRequest("id-token", null, null));
        Assert.Equal(ErrorCodes.Forbidden, login.ErrorCode);
        Assert.Equal(ErrorCodes.Forbidden, (await service.LinkGoogleAsync(Guid.NewGuid(), new LinkGoogleRequest("id-token"))).ErrorCode);
        await verifier.DidNotReceiveWithAnyArgs().VerifyGoogleTokenAsync(default!, default);

        await Publish(s => s.Set(PlatformSettingsCatalog.GoogleSignInEnabled, true));
        var again = await service.GoogleLoginAsync(new GoogleLoginRequest("id-token", null, null));
        Assert.NotEqual(ErrorCodes.Forbidden, again.ErrorCode); // reaches verification (which fails: the verifier returns null)
        await verifier.Received(1).VerifyGoogleTokenAsync("id-token", Arg.Any<CancellationToken>());
    }

    private (AuthServiceImpl Service, IUserRepository Users, IPasswordHasher Hasher) AuthService()
    {
        var unitOfWork = UnitOfWork(out var users);
        var hasher = Substitute.For<IPasswordHasher>();
        var service = new AuthServiceImpl(
            unitOfWork, hasher, Substitute.For<IJwtTokenGenerator>(), Substitute.For<IDistributedCache>(),
            Options.Create(new AuthSettings { DefaultRole = "user", MaxFailedAttempts = 5, LockoutDurationMinutes = 15 }),
            Substitute.For<ILogger<AuthServiceImpl>>(), Substitute.For<IWorkspaceInvitationClient>(), Substitute.For<IAuthEmailSender>(),
            platformSettings: _reader);
        return (service, users, hasher);
    }

    private static IUnitOfWork UnitOfWork(out IUserRepository users)
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        users = Substitute.For<IUserRepository>();
        unitOfWork.UserRepository.Returns(users);
        unitOfWork.RefreshTokenRepository.Returns(Substitute.For<IRefreshTokenRepository>());
        unitOfWork.UserSettingRepository.Returns(Substitute.For<IUserSettingRepository>());
        return unitOfWork;
    }
}
