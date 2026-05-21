using FluentValidation;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.Validators;

public class JoinTranslationRoomRequestValidator : AbstractValidator<JoinTranslationRoomRequest>
{
    public JoinTranslationRoomRequestValidator()
    {
        RuleFor(x => x.TranslationRoomCode)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationTranslationRoomCodeRequired)
            .Matches(@"^[a-z]{3}-[a-z]{4}-[a-z]{3}$").WithMessage(TranslationRoomConstants.ValidationTranslationRoomCodeFormat);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationDisplayNameRequired)
            .MaximumLength(100).WithMessage(TranslationRoomConstants.ValidationDisplayNameMaxLength);

        RuleFor(x => x.ListenLanguage)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationListenLanguageRequired);

        RuleFor(x => x.SpeakLanguage)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationSpeakLanguageRequired);
    }
}
