using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

/// <summary>
/// There was no validator for this request at all: /auth/reset-password bound
/// <see cref="ResetPasswordRequest"/> and used it unchecked, so the one route a locked-out user has
/// back into their account was also the one route with no rules on what it would accept.
///
/// It matters more than it looks now that login enforces a password ceiling. Reset is the recovery
/// path for anyone who set a longer password before that ceiling existed; if reset let them set
/// another over-long one, the recovery would hand them straight back into the same lockout.
/// </summary>
public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("Token is required.");

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordRequired)
            .MinimumLength(UserConstants.PasswordMinLength).WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordMinLength)
            .MaximumLength(UserConstants.PasswordMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordMaxLength);
    }
}
