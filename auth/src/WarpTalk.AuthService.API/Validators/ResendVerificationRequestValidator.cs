using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

/// <summary>
/// Kept character-for-character in step with <see cref="ForgotPasswordRequestValidator"/>.
///
/// See that class for why: these two endpoints answer identically on purpose so neither can be
/// used to discover which addresses have accounts, and a difference in what they REFUSE is a
/// difference in what they answer.
/// </summary>
public class ResendVerificationRequestValidator : AbstractValidator<ResendVerificationRequest>
{
    public ResendVerificationRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.EmailRequired)
            .MaximumLength(UserConstants.EmailMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.EmailMaxLength)
            .Matches(UserConstants.PermittedEmailRegex).WithMessage(ApiMessageConstants.ValidationMessages.EmailInvalidFormat);
    }
}
