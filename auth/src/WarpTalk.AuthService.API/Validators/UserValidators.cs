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
            .Matches(AuthConstants.LanguageCodeRegex).WithMessage(ApiMessageConstants.ValidationMessages.PreferredLanguageInvalid)
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

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordRequired)
            .MinimumLength(6).WithMessage(ApiMessageConstants.ValidationMessages.NewPasswordMinLength);
    }
}

public class UpdateUserSettingsRequestValidator : AbstractValidator<UpdateUserSettingsRequest>
{
    public UpdateUserSettingsRequestValidator()
    {
        RuleFor(x => x.TranscriptFontSize)
            .InclusiveBetween(AuthConstants.MinTranscriptFontSize, AuthConstants.MaxTranscriptFontSize)
            .WithMessage(string.Format(ApiMessageConstants.ValidationMessages.FontSizeOutOfBounds, AuthConstants.MinTranscriptFontSize, AuthConstants.MaxTranscriptFontSize))
            .When(x => x.TranscriptFontSize.HasValue);

        RuleFor(x => x.DefaultMaxParticipants)
            .InclusiveBetween(AuthConstants.MinMaxParticipants, AuthConstants.MaxMaxParticipants)
            .WithMessage(string.Format(ApiMessageConstants.ValidationMessages.MaxParticipantsOutOfBounds, AuthConstants.MinMaxParticipants, AuthConstants.MaxMaxParticipants))
            .When(x => x.DefaultMaxParticipants.HasValue);

        RuleFor(x => x.Theme)
            .Must(theme => theme != null && 
                (theme.ToLowerInvariant() == AuthConstants.ThemeLight || 
                 theme.ToLowerInvariant() == AuthConstants.ThemeDark || 
                 theme.ToLowerInvariant() == AuthConstants.ThemeSystem))
            .WithMessage(string.Format(ApiMessageConstants.ValidationMessages.InvalidTheme, AuthConstants.ThemeLight, AuthConstants.ThemeDark, AuthConstants.ThemeSystem))
            .When(x => x.Theme != null);

        RuleFor(x => x.DefaultTranslationRoomType)
            .Must(roomType => roomType != null && 
                (roomType.ToLowerInvariant() == AuthConstants.RoomTypeInstant || 
                 roomType.ToLowerInvariant() == AuthConstants.RoomTypeScheduled))
            .WithMessage(ApiMessageConstants.ValidationMessages.InvalidRoomType)
            .When(x => x.DefaultTranslationRoomType != null);

        RuleFor(x => x.DefaultSpeakLanguage)
            .Matches(AuthConstants.LanguageCodeRegex).WithMessage(ApiMessageConstants.ValidationMessages.InvalidSpeakLanguage)
            .When(x => x.DefaultSpeakLanguage != null);

        RuleFor(x => x.DefaultListenLanguage)
            .Matches(AuthConstants.LanguageCodeRegex).WithMessage(ApiMessageConstants.ValidationMessages.InvalidListenLanguage)
            .When(x => x.DefaultListenLanguage != null);
    }
}
