using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.API.Validators;

public class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator()
    {
        // Logout is idempotent and can clear a browser session even when its refresh cookie
        // has already expired. The controller accepts the cookie or legacy request body.
    }
}
