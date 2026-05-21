using FluentValidation;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.Validators;

public class UpdateRoomSettingsRequestValidator : AbstractValidator<UpdateRoomSettingsRequest>
{
    public UpdateRoomSettingsRequestValidator()
    {
        RuleFor(x => x.Settings)
            .NotNull().WithMessage(TranslationRoomConstants.ValidationSettingsRequired);

    }
}
