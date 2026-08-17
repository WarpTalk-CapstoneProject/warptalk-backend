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
            .Matches(UserConstants.PermittedEmailRegex).WithMessage(ApiMessageConstants.ValidationMessages.EmailInvalidFormat);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.PasswordRequired)
            .MinimumLength(6).WithMessage(ApiMessageConstants.ValidationMessages.PasswordMinLength);

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.FullNameRequired);

        // Shape only, not a whitelist. The catalogue of what WarpTalk can translate lives in the
        // web client and in the AI workers; duplicating it here would be a third copy that goes
        // stale the first time a language is added. What this stops is a free-text field arriving
        // in a column every meeting reads: anything that is not a language tag is rejected, and
        // anything absent falls back to the platform default in UserSettingsMapper.
        RuleFor(x => x.DefaultSpeakLanguage)
            .Matches(LanguageTagPattern).WithMessage(InvalidLanguageMessage)
            .When(x => !string.IsNullOrWhiteSpace(x.DefaultSpeakLanguage));

        RuleFor(x => x.DefaultListenLanguage)
            .Matches(LanguageTagPattern).WithMessage(InvalidLanguageMessage)
            .When(x => !string.IsNullOrWhiteSpace(x.DefaultListenLanguage));
    }

    /// <summary>BCP-47 as this product uses it: "vi", "en-US", "zh-Hans-CN".</summary>
    internal const string LanguageTagPattern = "^[A-Za-z]{2,8}(-[A-Za-z0-9]{2,8}){0,2}$";

    internal const string InvalidLanguageMessage = "Language must be a valid language tag, for example en-US.";
}
