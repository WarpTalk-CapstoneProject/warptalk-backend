using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Helpers;

public static class AuthResultHelper
{
    public static IActionResult HandleAuthFailure(ControllerBase controller, Result<AuthResponse> result)
    {
        if (result.ErrorCode == ErrorCodes.AccountInactive || result.ErrorCode == ErrorCodes.AccountLocked)
        {
            return controller.BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return controller.Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
    }
}
