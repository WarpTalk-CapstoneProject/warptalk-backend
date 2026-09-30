using WarpTalk.Shared.PlatformSettings;
using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
        : this(null)
    {
    }

    /// <param name="settings">
    /// Live minimum length (security.password.min_length). Validators are created per request, so
    /// the value read here is the one in force for this request.
    /// </param>
    public ChangePasswordRequestValidator(IPlatformSettings? settings)
    {
        var minLength = PasswordPolicy.MinLength(settings);
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordRequired)
            .MinimumLength(minLength).WithMessage(PasswordPolicy.NewPasswordMessage(minLength))
            .MaximumLength(UserConstants.PasswordMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordMaxLength);
    }
}
