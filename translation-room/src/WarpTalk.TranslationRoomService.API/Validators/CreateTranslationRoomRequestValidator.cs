using FluentValidation;
using System;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.Validators;

public class CreateTranslationRoomRequestValidator : AbstractValidator<CreateTranslationRoomRequest>
{
    public CreateTranslationRoomRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationMessages.TitleRequired)
            .MaximumLength(TranslationRoomConstants.MaxTitleLength).WithMessage(TranslationRoomConstants.ValidationMessages.TitleMaxLength);

        RuleFor(x => x.SourceLanguage)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationMessages.SourceLanguageRequired);

        RuleFor(x => x.TargetLanguages)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationMessages.TargetLanguagesRequired);

        RuleFor(x => x.MaxParticipants)
            .GreaterThan(0).WithMessage(TranslationRoomConstants.ValidationMessages.MaxParticipantsGreaterThanZero);

        RuleFor(x => x.ScheduledAt)
            .Must(scheduledAt => scheduledAt.HasValue && scheduledAt.Value > DateTime.UtcNow)
            .When(x => x.ScheduledAt.HasValue)
            .WithMessage(TranslationRoomConstants.ValidationMessages.ScheduledTimeMustBeFuture);
    }
}
