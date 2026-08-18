using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.AuthService.API.Controllers;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// WT-361 — what a failed Google sign-in is allowed to SAY.
///
/// The bug report for this endpoint was, in its entirety, "400 Bad Request". That is not a
/// coincidence: every failure returned 400, so a database blip, an unreachable Google, and a
/// misconfigured OAuth client id were indistinguishable from a token we looked at and refused.
/// There was no way to tell, from the outside, which had happened — and the endpoint's own log
/// line for the most likely cause named neither the expected client id nor the reported one.
///
/// TokenController.Refresh already learned this in WT-344 and carries a comment explaining it.
/// These tests hold google-login to the same rule: a failure to ANSWER is not a verdict on the
/// credential.
/// </summary>
public class GoogleLoginFailureShapeTests
{
    private readonly IGoogleAuthService _googleAuthService = Substitute.For<IGoogleAuthService>();

    private GoogleAuthController CreateController()
    {
        var controller = new GoogleAuthController(_googleAuthService);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return controller;
    }

    private async Task<IActionResult> LoginReturning(Result<AuthResponse> result)
    {
        _googleAuthService
            .GoogleLoginAsync(Arg.Any<GoogleLoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(result);

        return await CreateController()
            .GoogleLogin(new GoogleLoginRequest("a-token", null, null), CancellationToken.None);
    }

    [Theory]
    [InlineData(ErrorCodes.InternalServerError)]
    [InlineData(ErrorCodes.ServiceUnavailable)]
    public async Task A_fault_on_our_side_is_not_reported_as_a_bad_request(string errorCode)
    {
        // "We could not check" must never be dressed up as "your credential is bad". The user
        // did nothing wrong, the credential is probably fine, and the browser should retry.
        var response = await LoginReturning(
            Result.Failure<AuthResponse>("Google is unreachable.", errorCode));

        var status = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Theory]
    [InlineData(ErrorCodes.InvalidToken)]
    [InlineData(ErrorCodes.InvalidCredentials)]
    [InlineData(ErrorCodes.EmailNotVerified)]
    public async Task A_credential_we_looked_at_and_refused_is_still_a_bad_request(string errorCode)
    {
        // The other half of the rule. Widening 503 to cover real rejections would tell the client
        // to retry a token that will never work, and hide a genuine sign-in problem behind a
        // spinner.
        var response = await LoginReturning(
            Result.Failure<AuthResponse>("Nope.", errorCode));

        Assert.IsType<BadRequestObjectResult>(response);
    }

    [Fact]
    public async Task The_reason_survives_into_the_response_body()
    {
        // Whatever the status, the caller has to be able to find out WHY — that is the whole
        // complaint behind this ticket.
        var response = await LoginReturning(
            Result.Failure<AuthResponse>("Email not verified.", ErrorCodes.EmailNotVerified));

        var badRequest = Assert.IsType<BadRequestObjectResult>(response);
        var body = Assert.IsType<ApiErrorResponse>(badRequest.Value);
        Assert.Equal("Email not verified.", body.Error);
        Assert.Equal(ErrorCodes.EmailNotVerified, body.Code);
    }
}
