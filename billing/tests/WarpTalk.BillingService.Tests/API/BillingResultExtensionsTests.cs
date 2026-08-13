using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.API;

/// <summary>
/// Which HTTP status a billing failure gets.
///
/// This is pinned because the distinction is invisible from inside the service and very visible
/// from outside it. Every read here used to answer failure with 400, so a workspace that simply
/// had no subscription was reported as a malformed request — and the owner dashboard, unable to
/// tell the two apart, rendered "Failed to load chart data" as its permanent state on any
/// workspace without a plan.
///
/// The opposite conflation matters just as much: the web client never retries a 4xx, by design,
/// so a genuine server fault dressed as 400 was permanent to the user rather than transient.
/// </summary>
public class BillingResultExtensionsTests
{
    private readonly ControllerBase _controller = new TestController();

    [Fact]
    public void ASuccessIsAnOk()
    {
        var action = _controller.ToActionResult(Result.Success("value"));

        action.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be("value");
    }

    [Theory]
    [InlineData(ErrorCodes.BillingSubscriptionNotFound)]
    [InlineData(ErrorCodes.BillingPlanNotFound)]
    [InlineData(ErrorCodes.BillingWorkspaceNotFound)]
    [InlineData(ErrorCodes.BillingTransactionNotFound)]
    [InlineData(ErrorCodes.NotFound)]
    public void AMissingRecordIsA404(string errorCode)
    {
        // "This workspace has no plan" is an account state the UI can name and act on. As a 400 it
        // is indistinguishable from a bug in the caller.
        StatusOf(errorCode).Should().Be(404);
    }

    [Fact]
    public void ARefusalIsA403()
    {
        StatusOf(ErrorCodes.Forbidden).Should().Be(403);
    }

    [Fact]
    public void AServiceFaultIsA500()
    {
        // Not 400: the client's retry policy skips every 4xx, so a transient fault reported as one
        // never gets the second attempt that would have succeeded.
        StatusOf(ErrorCodes.InternalServerError).Should().Be(500);
    }

    [Theory]
    [InlineData(ErrorCodes.ValidationError)]
    [InlineData("SOMETHING_NOBODY_HAS_MAPPED_YET")]
    public void AnythingElseStaysA400(string errorCode)
    {
        StatusOf(errorCode).Should().Be(400);
    }

    [Fact]
    public void AFailureCarriesItsErrorCodeToTheClient()
    {
        // The status alone does not tell a client WHICH thing was missing, and the dashboard
        // branches on the code to decide between "choose a plan" and "not found".
        var action = _controller.ToActionResult(
            Result.Failure<string>("No subscription", ErrorCodes.BillingSubscriptionNotFound));

        var body = ((ObjectResult)action.Result!).Value.Should().BeOfType<ApiErrorResponse>().Subject;
        body.Code.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
        body.Error.Should().Be("No subscription");
    }

    [Fact]
    public void AFailureWithNoMessageStillSaysSomething()
    {
        var action = _controller.ToActionResult(
            Result.Failure<string>(null!, ErrorCodes.InternalServerError));

        ((ApiErrorResponse)((ObjectResult)action.Result!).Value!)
            .Error.Should().Be(ApiMessageConstants.ErrorMessages.BillingInternalError);
    }

    private int StatusOf(string errorCode)
    {
        var action = _controller.ToActionResult(Result.Failure<string>("failed", errorCode));
        return ((ObjectResult)action.Result!).StatusCode!.Value;
    }

    /// <summary>ControllerBase is abstract; the extension only needs its result helpers.</summary>
    private sealed class TestController : ControllerBase;
}
