using System;
using System.Data.Common;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// WT-596 — an outage must not reach the browser as "Bad Request".
///
/// On 30–31/08/2026 Postgres was unreachable for 27 hours and `POST /api/v1/auth/login` answered
/// 400 with "An unexpected error occurred during login." Two things followed from that status:
/// alerting treats 4xx as a client error and never paged, and every human who looked went
/// straight to the login validator — which had not run, because the request never got that far.
///
/// These tests pin the two halves of the fix: the service says WHICH kind of failure it was, and
/// the shared mapping turns that into a status that means "we could not answer".
/// </summary>
public class InfrastructureFailureStatusTests
{
    /// <summary>A Npgsql-shaped failure, without depending on Npgsql: SocketException inside a
    /// DbException is exactly what "Connection refused" arrives as through EF.</summary>
    private sealed class UnreachableDatabaseException : DbException
    {
        public UnreachableDatabaseException()
            : base("Failed to connect to 10.20.0.20:6432", new SocketException(111))
        {
        }
    }

    private static WarpTalk.AuthService.Application.Services.AuthService BuildServiceThatCannotReachTheDatabase()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var userRepository = Substitute.For<IUserRepository>();
        unitOfWork.UserRepository.Returns(userRepository);
        unitOfWork.RefreshTokenRepository.Returns(Substitute.For<IRefreshTokenRepository>());
        unitOfWork.UserSettingRepository.Returns(Substitute.For<IUserSettingRepository>());

        userRepository
            .GetByEmailWithRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new UnreachableDatabaseException());

        return new WarpTalk.AuthService.Application.Services.AuthService(
            unitOfWork,
            Substitute.For<IPasswordHasher>(),
            Substitute.For<IJwtTokenGenerator>(),
            Substitute.For<IDistributedCache>(),
            Options.Create(new AuthSettings()),
            Substitute.For<ILogger<WarpTalk.AuthService.Application.Services.AuthService>>(),
            Substitute.For<IWorkspaceInvitationClient>(),
            Substitute.For<IAuthEmailSender>());
    }

    [Fact]
    public async Task LoginAsync_WhenTheDatabaseIsUnreachable_ReportsServiceUnavailable()
    {
        var service = BuildServiceThatCannotReachTheDatabase();

        var result = await service.LoginAsync(new LoginRequest("someone@warptalk.vn", "whatever", null, null));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ServiceUnavailable, result.ErrorCode);
        // The message has to say it is not about the credentials, because it is rendered straight
        // into the toast under the password field.
        Assert.Contains("not a problem with your details", result.Error);
    }

    [Fact]
    public void ServiceUnavailable_IsReportedAs503_NotAs400()
    {
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ApiErrorStatus.For(ErrorCodes.ServiceUnavailable));
        Assert.Equal(StatusCodes.Status500InternalServerError, ApiErrorStatus.For(ErrorCodes.InternalServerError));
    }

    /// <summary>
    /// The half that is easy to lose again: a REFUSAL still has to be 4xx. A mapping that answered
    /// 5xx for everything would hide the opposite mistake just as well.
    /// </summary>
    [Theory]
    [InlineData(ErrorCodes.InvalidCredentials, StatusCodes.Status401Unauthorized)]
    [InlineData(ErrorCodes.EmailExists, StatusCodes.Status409Conflict)]
    [InlineData(ErrorCodes.ValidationError, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.AccountPending, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.RateLimitExceeded, StatusCodes.Status429TooManyRequests)]
    public void RefusalsKeepTheir4xxStatus(string errorCode, int expected)
    {
        Assert.Equal(expected, ApiErrorStatus.For(errorCode));
    }

    [Fact]
    public void AnOrdinaryBugIsNotMistakenForAnOutage()
    {
        // A NullReferenceException is ours to fix, not a dependency being down. Classifying it as
        // ServiceUnavailable would tell the caller to retry something that will never succeed.
        Assert.Equal(ErrorCodes.InternalServerError, InfrastructureFailure.ClassifyErrorCode(new NullReferenceException()));
        Assert.Equal(ErrorCodes.ServiceUnavailable, InfrastructureFailure.ClassifyErrorCode(new UnreachableDatabaseException()));

        // EF wraps a driver failure; the classifier has to walk in to find it.
        Assert.Equal(
            ErrorCodes.ServiceUnavailable,
            InfrastructureFailure.ClassifyErrorCode(
                new InvalidOperationException("An error occurred using the connection to database", new UnreachableDatabaseException())));
    }
}
