using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.EmailRequired)
            .MaximumLength(UserConstants.EmailMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.EmailMaxLength)
            .Matches(UserConstants.PermittedEmailRegex).WithMessage(ApiMessageConstants.ValidationMessages.EmailInvalidFormat);

        // Deliberately no MinimumLength: login must not tell an attacker how long the real password
        // is, and an existing account may predate any floor we set. The maximum is different — it
        // stops a caller handing the hasher a megabyte of text, and it is safe to add here ONLY
        // because every path that writes a password now enforces the same ceiling.
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.PasswordRequired)
            .MaximumLength(UserConstants.PasswordMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.PasswordMaxLength);
    }
}
