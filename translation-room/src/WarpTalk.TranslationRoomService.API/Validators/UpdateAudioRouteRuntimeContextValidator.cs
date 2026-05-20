using FluentValidation;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.Validators;

public class UpdateAudioRouteRuntimeContextValidator : AbstractValidator<UpdateAudioRouteRuntimeContextDto>
{
    public UpdateAudioRouteRuntimeContextValidator()
    {
        RuleFor(x => x.StreamId)
            .NotEmpty().When(x => x.Status == AudioRouteStatus.AUDIO_ROUTING_ACTIVE)
            .WithMessage(AudioRouteConstants.ValidationStreamIdRequiredForActive);
    }
}
