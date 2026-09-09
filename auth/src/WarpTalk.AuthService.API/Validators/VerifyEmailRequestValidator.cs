using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

/// <summary>
/// /auth/verify-email had no validator either. The token is never stored as sent — it is SHA-256'd
/// and the hash is compared — so an over-long one could not overflow a column. What it could do is
/// make an unauthenticated caller's arbitrary payload the input to a hash on every request.
///
/// The message for an over-long token is deliberately the same shapeless "Token is not valid." a
/// wrong token gets. A token is not something a person typed and can correct, and telling a caller
/// which of "too long" and "not recognised" applies only helps someone probing the endpoint.
/// </summary>
public class VerifyEmailRequestValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailRequestValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.TokenRequired)
            .MaximumLength(UserConstants.TokenMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.TokenMaxLength);
    }
}
