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
            .MinimumLength(UserConstants.PasswordMinLength).WithMessage(ApiMessageConstants.ValidationMessages.PasswordMinLength)
            .MaximumLength(UserConstants.PasswordMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.PasswordMaxLength);

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.FullNameRequired)
            .MaximumLength(UserConstants.FullNameMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.FullNameMaxLength);

        // RegisterInvitedRequest carries the same two optional language fields as RegisterRequest
        // and, until now, validated neither — so the invited path was the one way to put free text
        // into a column every meeting reads.
        RuleFor(x => x.DefaultSpeakLanguage)
            .Matches(UserConstants.LanguageTagRegex).WithMessage(ApiMessageConstants.ValidationMessages.LanguageTagInvalid)
            .When(x => !string.IsNullOrWhiteSpace(x.DefaultSpeakLanguage));

        RuleFor(x => x.DefaultListenLanguage)
            .Matches(UserConstants.LanguageTagRegex).WithMessage(ApiMessageConstants.ValidationMessages.LanguageTagInvalid)
            .When(x => !string.IsNullOrWhiteSpace(x.DefaultListenLanguage));
    }
}
