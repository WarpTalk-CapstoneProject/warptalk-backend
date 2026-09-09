using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.EmailRequired)
            .MaximumLength(UserConstants.EmailMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.EmailMaxLength)
            .Matches(UserConstants.PermittedEmailRegex).WithMessage(ApiMessageConstants.ValidationMessages.EmailInvalidFormat);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.PasswordRequired)
            .MinimumLength(UserConstants.PasswordMinLength).WithMessage(ApiMessageConstants.ValidationMessages.PasswordMinLength)
            .MaximumLength(UserConstants.PasswordMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.PasswordMaxLength);

        // The ceiling matters more than the floor here. full_name is varchar(150), and without this
        // rule an over-long name travelled all the way to SaveChangesAsync and came back to the
        // caller as an unexplained server error — the one failure mode a registration form cannot
        // ask the user to do anything about.
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.FullNameRequired)
            .MaximumLength(UserConstants.FullNameMaxLength).WithMessage(ApiMessageConstants.ValidationMessages.FullNameMaxLength);

        RuleFor(x => x.DefaultSpeakLanguage)
            .Matches(UserConstants.LanguageTagRegex).WithMessage(ApiMessageConstants.ValidationMessages.LanguageTagInvalid)
            .When(x => !string.IsNullOrWhiteSpace(x.DefaultSpeakLanguage));

        RuleFor(x => x.DefaultListenLanguage)
            .Matches(UserConstants.LanguageTagRegex).WithMessage(ApiMessageConstants.ValidationMessages.LanguageTagInvalid)
            .When(x => !string.IsNullOrWhiteSpace(x.DefaultListenLanguage));
    }
}
