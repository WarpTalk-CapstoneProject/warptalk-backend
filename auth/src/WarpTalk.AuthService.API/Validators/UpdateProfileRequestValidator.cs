using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.FullNameNotEmpty)
            .When(x => x.FullName != null);

        RuleFor(x => x.PreferredLanguage)
            .Matches(UserConstants.LanguageCodeRegex).WithMessage(ApiMessageConstants.ValidationMessages.PreferredLanguageInvalid)
            .When(x => x.PreferredLanguage != null);

        RuleFor(x => x.Timezone)
            .Must(timezone =>
            {
                if (timezone == null) return true;

                // UTC is a valid standard ID
                if (timezone.Equals("UTC", System.StringComparison.OrdinalIgnoreCase)) return true;

                // Strictly enforce IANA by trying to convert it to Windows ID
                return System.TimeZoneInfo.TryConvertIanaIdToWindowsId(timezone, out _);
            })
            .WithMessage(ApiMessageConstants.ValidationMessages.TimezoneInvalid)
            .When(x => x.Timezone != null);
    }
}
