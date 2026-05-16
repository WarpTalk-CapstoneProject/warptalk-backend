using FluentValidation;
using System;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.API.Validators;

public class CreateTranslationRoomRequestValidator : AbstractValidator<CreateTranslationRoomRequest>
{
    public CreateTranslationRoomRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.TitleRequired)
            .MaximumLength(255).WithMessage(ApiMessageConstants.ValidationMessages.TitleMaxLength);

        RuleFor(x => x.SourceLanguage)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationSourceLanguageRequired);

        RuleFor(x => x.TargetLanguages)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationTargetLanguagesRequired);

        RuleFor(x => x.MaxParticipants)
            .GreaterThan(0).WithMessage(TranslationRoomConstants.ValidationMaxParticipantsGreaterThanZero);

        RuleFor(x => x.ScheduledAt)
            .Must(scheduledAt => scheduledAt.HasValue && scheduledAt.Value > DateTime.UtcNow)
            .When(x => x.ScheduledAt.HasValue)
            .WithMessage(TranslationRoomConstants.ValidationScheduledTimeMustBeFuture);
    }
}
