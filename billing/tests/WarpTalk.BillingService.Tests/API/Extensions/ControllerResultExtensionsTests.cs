using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Extensions;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.API.Extensions;

public class ControllerResultExtensionsTests
{
    private sealed class TestController : ControllerBase;

    [Fact]
    public void ToActionResult_Should_Return_Ok_For_Success()
    {
        var controller = new TestController();
        var actionResult = Result.Success("ok").ToActionResult(controller);

        actionResult.Result.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)actionResult.Result!).Value.Should().Be("ok");
    }

    [Fact]
    public void ToActionResult_Should_Return_BadRequest_ApiError_For_Failure()
    {
        var controller = new TestController();
        var actionResult = Result.Failure<string>("failed", ErrorCodes.ValidationError).ToActionResult(controller);

        var badRequest = actionResult.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var error = badRequest.Value.Should().BeOfType<ApiErrorResponse>().Subject;
        error.Error.Should().Be("failed");
        error.Code.Should().Be(ErrorCodes.ValidationError);
    }
}
