using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Extensions;

public static class ControllerResultExtensions
{
    public static ActionResult<T> ToActionResult<T>(
        this Result<T> result,
        ControllerBase controller,
        Func<T, ActionResult<T>>? onSuccess = null)
    {
        if (!result.IsSuccess)
            return controller.BadRequest(ToError(result.Error, result.ErrorCode));

        return onSuccess is null
            ? controller.Ok(result.Value!)
            : onSuccess(result.Value!);
    }

    public static IActionResult ToActionResult(
        this Result result,
        ControllerBase controller,
        Func<IActionResult>? onSuccess = null)
    {
        if (!result.IsSuccess)
            return controller.BadRequest(ToError(result.Error, result.ErrorCode));

        return onSuccess is null ? controller.NoContent() : onSuccess();
    }

    public static ObjectResult ToErrorResult(
        this ControllerBase controller,
        int statusCode,
        string? error,
        string? errorCode)
        => controller.StatusCode(statusCode, ToError(error, errorCode));

    public static BadRequestObjectResult ToBadRequest(
        this ControllerBase controller,
        string? error,
        string? errorCode)
        => controller.BadRequest(ToError(error, errorCode));

    public static ApiErrorResponse ToErrorResponse(string? error, string? errorCode)
        => ToError(error, errorCode);

    private static ApiErrorResponse ToError(string? error, string? errorCode)
        => new(error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, errorCode);
}
