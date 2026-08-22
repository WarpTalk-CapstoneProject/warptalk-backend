using FluentValidation;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.Validators;

/// <summary>
/// WT-555. The same rules as <see cref="JoinTranslationRoomRequestValidator"/> minus the room
/// code, which a by-id join does not carry and must not be asked for.
/// </summary>
public class JoinTranslationRoomByIdRequestValidator : AbstractValidator<JoinTranslationRoomByIdRequest>
{
    public JoinTranslationRoomByIdRequestValidator()
    {
        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationDisplayNameRequired)
            .MaximumLength(100).WithMessage(TranslationRoomConstants.ValidationDisplayNameMaxLength);

        RuleFor(x => x.ListenLanguage)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationListenLanguageRequired);

        RuleFor(x => x.SpeakLanguage)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationSpeakLanguageRequired);
    }
}
