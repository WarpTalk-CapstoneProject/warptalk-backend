using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

/// <summary>
/// /auth/forgot-password had no validator, so an unauthenticated caller could hand it a string of
/// any size and shape and have it normalised and used as a lookup key.
///
/// These rules are deliberately IDENTICAL to <see cref="ResendVerificationRequestValidator"/>.
/// The two endpoints are documented as answering the same way whatever the address turns out to
/// be, precisely so that neither can be used to find out which addresses have accounts. Validating
/// them differently would reintroduce that difference through the back door: an address one
/// endpoint refuses at 400 and the other accepts at 204 is a signal, and it would be a signal
/// nobody had decided to send.
///
/// Refusing a MALFORMED address is safe on that count — it tells the caller only what they already
/// know about the string they typed, not whether an account exists.
/// </summary>
public class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.EmailRequired)
            .MaximumLength(UserConstants.EmailMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.EmailMaxLength)
            .Matches(UserConstants.PermittedEmailRegex).WithMessage(ApiMessageConstants.ValidationMessages.EmailInvalidFormat);
    }
}
