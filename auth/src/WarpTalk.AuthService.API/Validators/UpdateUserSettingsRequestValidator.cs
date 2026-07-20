using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class UpdateUserSettingsRequestValidator : AbstractValidator<UpdateUserSettingsRequest>
{
    public UpdateUserSettingsRequestValidator()
    {
        RuleFor(x => x.TranscriptFontSize)
            .InclusiveBetween(UserConstants.MinTranscriptFontSize, UserConstants.MaxTranscriptFontSize)
            .WithMessage(string.Format(ApiMessageConstants.ValidationMessages.FontSizeOutOfBounds, UserConstants.MinTranscriptFontSize, UserConstants.MaxTranscriptFontSize))
            .When(x => x.TranscriptFontSize.HasValue);

        RuleFor(x => x.DefaultMaxParticipants)
            .InclusiveBetween(UserConstants.MinMaxParticipants, UserConstants.MaxMaxParticipants)
            .WithMessage(string.Format(ApiMessageConstants.ValidationMessages.MaxParticipantsOutOfBounds, UserConstants.MinMaxParticipants, UserConstants.MaxMaxParticipants))
            .When(x => x.DefaultMaxParticipants.HasValue);

        RuleFor(x => x.Theme)
            .Must(theme => theme != null && 
                (theme.ToLowerInvariant() == UserConstants.ThemeLight || 
                 theme.ToLowerInvariant() == UserConstants.ThemeDark || 
                 theme.ToLowerInvariant() == UserConstants.ThemeSystem))
            .WithMessage(string.Format(ApiMessageConstants.ValidationMessages.InvalidTheme, UserConstants.ThemeLight, UserConstants.ThemeDark, UserConstants.ThemeSystem))
            .When(x => x.Theme != null);

        RuleFor(x => x.DefaultTranslationRoomType)
            .Must(roomType => roomType != null && 
                (roomType.ToLowerInvariant() == UserConstants.RoomTypeInstant || 
                 roomType.ToLowerInvariant() == UserConstants.RoomTypeScheduled))
            .WithMessage(ApiMessageConstants.ValidationMessages.InvalidRoomType)
            .When(x => x.DefaultTranslationRoomType != null);

        RuleFor(x => x.DefaultSpeakLanguage)
            .Matches(UserConstants.LanguageCodeRegex).WithMessage(ApiMessageConstants.ValidationMessages.InvalidSpeakLanguage)
            .When(x => x.DefaultSpeakLanguage != null);

        RuleFor(x => x.DefaultListenLanguage)
            .Matches(UserConstants.LanguageCodeRegex).WithMessage(ApiMessageConstants.ValidationMessages.InvalidListenLanguage)
            .When(x => x.DefaultListenLanguage != null);
    }
}
