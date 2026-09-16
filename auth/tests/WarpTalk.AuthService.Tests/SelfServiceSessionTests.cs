using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AuthService.API.Controllers;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// Personal "Sessions &amp; devices": list my sessions, end one, end all the others.
/// A session is a refresh-token family; "this device" is the family of the request's refresh cookie.
/// </summary>
public class SelfServiceSessionTests
{
    private const string CurrentToken = "current-refresh-token";

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IRefreshTokenRepository _tokens = Substitute.For<IRefreshTokenRepository>();
    private readonly TokenService _service;

    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _someoneElse = Guid.NewGuid();
    private readonly Guid _currentFamily = Guid.NewGuid();
    private readonly Guid _laptopFamily = Guid.NewGuid();

    public SelfServiceSessionTests()
    {
        _unitOfWork.RefreshTokenRepository.Returns(_tokens);
        _unitOfWork.UserRepository.Returns(Substitute.For<IUserRepository>());
        _service = new TokenService(
            _unitOfWork,
            Substitute.For<IJwtTokenGenerator>(),
            Options.Create(new AuthSettings { DefaultRole = "User" }),
            Substitute.For<ILogger<TokenService>>());

        var now = DateTime.UtcNow;
        _tokens.GetActiveSessionsForUserAsync(_me, Arg.Any<CancellationToken>()).Returns(
            new List<UserSessionRow>
            {
                new(_laptopFamily, "Firefox", "10.0.0.2", now.AddDays(-3), now.AddMinutes(-5), now.AddDays(7)),
                new(_currentFamily, "Chrome", "10.0.0.1", now.AddDays(-1), now.AddMinutes(-20), now.AddDays(7)),
            });
        _tokens.GetActiveSessionsForUserAsync(_someoneElse, Arg.Any<CancellationToken>())
            .Returns(new List<UserSessionRow>());
        _tokens.GetByTokenHashAsync(TokenHasher.Hash(CurrentToken), Arg.Any<CancellationToken>())
            .Returns(new RefreshToken { UserId = _me, FamilyId = _currentFamily, TokenHash = "x" });
    }

    [Fact]
    public async Task List_marks_the_session_of_the_request_cookie_as_current_and_puts_it_first()
    {
        var result = await _service.GetSessionsAsync(_me, CurrentToken);

        Assert.True(result.IsSuccess);
        var sessions = result.Value!;
        Assert.Equal(2, sessions.Count);
        Assert.Equal(_currentFamily, sessions[0].Id);
        Assert.True(sessions[0].IsCurrent);
        Assert.False(sessions[1].IsCurrent);
    }

    [Fact]
    public async Task List_without_a_cookie_marks_nothing_current()
    {
        var result = await _service.GetSessionsAsync(_me, null);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Value!, s => s.IsCurrent);
    }

    [Fact]
    public async Task A_refresh_token_owned_by_someone_else_never_marks_a_session_current()
    {
        _tokens.GetByTokenHashAsync(TokenHasher.Hash("stolen"), Arg.Any<CancellationToken>())
            .Returns(new RefreshToken { UserId = _someoneElse, FamilyId = _laptopFamily, TokenHash = "y" });

        var result = await _service.GetSessionsAsync(_me, "stolen");

        Assert.DoesNotContain(result.Value!, s => s.IsCurrent);
    }

    [Fact]
    public async Task Revoking_another_of_my_sessions_revokes_its_whole_family()
    {
        var result = await _service.RevokeSessionAsync(_me, _laptopFamily, CurrentToken);

        Assert.True(result.IsSuccess);
        await _tokens.Received(1).RevokeFamilyAsync(_laptopFamily, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revoking_another_users_session_is_not_found_and_revokes_nothing()
    {
        // _laptopFamily is mine; to someone else it must be indistinguishable from a made-up id.
        var result = await _service.RevokeSessionAsync(_someoneElse, _laptopFamily, null);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _tokens.DidNotReceiveWithAnyArgs().RevokeFamilyAsync(default, default);
    }

    [Fact]
    public async Task Revoking_an_unknown_session_is_not_found()
    {
        var result = await _service.RevokeSessionAsync(_me, Guid.NewGuid(), CurrentToken);

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _tokens.DidNotReceiveWithAnyArgs().RevokeFamilyAsync(default, default);
    }

    [Fact]
    public async Task Revoking_the_current_session_is_refused_so_it_ends_through_logout()
    {
        var result = await _service.RevokeSessionAsync(_me, _currentFamily, CurrentToken);

        Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
        await _tokens.DidNotReceiveWithAnyArgs().RevokeFamilyAsync(default, default);
    }

    [Fact]
    public async Task Sign_out_others_keeps_the_current_family()
    {
        var result = await _service.RevokeOtherSessionsAsync(_me, CurrentToken);

        Assert.True(result.IsSuccess);
        await _tokens.Received(1).RevokeAllForUserExceptFamilyAsync(_me, _currentFamily, Arg.Any<CancellationToken>());
        await _tokens.DidNotReceiveWithAnyArgs().RevokeAllForUserAsync(default, default);
    }

    [Fact]
    public async Task Sign_out_others_refuses_when_the_current_session_cannot_be_identified()
    {
        var result = await _service.RevokeOtherSessionsAsync(_me, null);

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        await _tokens.DidNotReceiveWithAnyArgs().RevokeAllForUserExceptFamilyAsync(default, default, default);
        await _tokens.DidNotReceiveWithAnyArgs().RevokeAllForUserAsync(default, default);
    }

    [Theory]
    [InlineData("NOT_FOUND", 404)]
    [InlineData("CONFLICT", 409)]
    [InlineData("VALIDATION_ERROR", 400)]
    [InlineData("INTERNAL_SERVER_ERROR", 500)]
    public async Task Controller_maps_revoke_failures_to_status_codes(string code, int status)
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.RevokeSessionAsync(_me, Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("nope", code));

        var response = await Controller(tokenService).RevokeSession(Guid.NewGuid(), CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(response);
        Assert.Equal(status, objectResult.StatusCode);
    }

    [Fact]
    public async Task Controller_passes_the_refresh_cookie_as_the_current_session()
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.GetSessionsAsync(_me, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<UserSessionDto>>(Array.Empty<UserSessionDto>()));

        var response = await Controller(tokenService, CurrentToken).GetSessions(CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        await tokenService.Received(1).GetSessionsAsync(_me, CurrentToken, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Controller_revoke_success_is_no_content()
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.RevokeOtherSessionsAsync(_me, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var response = await Controller(tokenService, CurrentToken).RevokeOtherSessions(CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
    }

    private SessionsController Controller(ITokenService tokenService, string? refreshCookie = null)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, _me.ToString()), new Claim("sub", _me.ToString()) },
                "test")),
        };
        if (refreshCookie is not null)
            context.Request.Headers.Cookie = $"warptalk_refresh={refreshCookie}";

        return new SessionsController(tokenService)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }
}
