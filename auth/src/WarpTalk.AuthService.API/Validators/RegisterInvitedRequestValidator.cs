using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class RegisterInvitedRequestValidator : AbstractValidator<RegisterInvitedRequest>
{
    public RegisterInvitedRequestValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("Token is required.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.PasswordRequired)
            .MinimumLength(6).WithMessage(ApiMessageConstants.ValidationMessages.PasswordMinLength);

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.FullNameRequired);
    }
}
