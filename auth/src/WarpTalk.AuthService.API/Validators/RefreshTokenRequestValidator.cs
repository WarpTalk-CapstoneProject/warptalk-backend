using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.API.Validators;

public class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        // The refresh token normally arrives through the HttpOnly cookie. The controller
        // rejects the request only when both the cookie and legacy request body are empty.
    }
}
