using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordRequired)
            .MinimumLength(6).WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordMinLength);
    }
}
