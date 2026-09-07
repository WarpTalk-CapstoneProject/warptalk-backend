using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordRequired)
            .MinimumLength(UserConstants.PasswordMinLength).WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordMinLength)
            .MaximumLength(UserConstants.PasswordMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordMaxLength);
    }
}
