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
        RuleFor(x => x.WorkspaceId)
            .Must(workspaceId => workspaceId.HasValue && workspaceId.Value != Guid.Empty)
            .WithMessage(ApiMessageConstants.ValidationMessages.WorkspaceRequired);

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.TitleRequired)
            .MaximumLength(255).WithMessage(ApiMessageConstants.ValidationMessages.TitleMaxLength);

        RuleFor(x => x.SourceLanguage)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationSourceLanguageRequired);

        RuleFor(x => x.TargetLanguages)
            .NotEmpty().WithMessage(TranslationRoomConstants.ValidationTargetLanguagesRequired);

        // Optional now: omitted means "let the meeting type decide" (see
        // TranslationRoomTypePolicy). Only a value that IS sent has to make sense.
        RuleFor(x => x.MaxParticipants)
            .GreaterThan(0).WithMessage(TranslationRoomConstants.ValidationMaxParticipantsGreaterThanZero)
            .When(x => x.MaxParticipants.HasValue);

        // The type drives real behaviour now (lobby, mute-on-entry, recording, breakouts,
        // seats), so an unrecognised one must not be stored — it would silently fall back to
        // the neutral profile and look like the picked type did nothing, which is exactly the
        // bug this replaced.
        RuleFor(x => x.TranslationRoomType)
            .Must(type => string.IsNullOrWhiteSpace(type) || TranslationRoomTypes.Normalize(type) != null)
            .WithMessage(TranslationRoomConstants.ValidationRoomTypeUnsupported);

        RuleFor(x => x.ScheduledAt)
            .Must(scheduledAt => scheduledAt.HasValue && scheduledAt.Value > DateTime.UtcNow)
            .When(x => x.ScheduledAt.HasValue)
            .WithMessage(TranslationRoomConstants.ValidationScheduledTimeMustBeFuture);
    }
}
