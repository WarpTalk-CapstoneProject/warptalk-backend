using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>One mapping from a failed <see cref="Result"/> to an HTTP status, for the CMS controllers.</summary>
internal static class ControllerResults
{
    public static IActionResult Failure(ControllerBase controller, string? error, string? errorCode)
    {
        var body = new ApiErrorResponse(error, errorCode);
        return errorCode switch
        {
            ErrorCodes.NotFound => controller.NotFound(body),
            ErrorCodes.Conflict or ErrorCodes.InvalidState => controller.Conflict(body),
            ErrorCodes.ServiceUnavailable => controller.StatusCode(StatusCodes.Status503ServiceUnavailable, body),
            ErrorCodes.Forbidden => controller.StatusCode(StatusCodes.Status403Forbidden, body),
            _ => controller.BadRequest(body),
        };
    }
}
