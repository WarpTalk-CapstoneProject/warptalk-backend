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

        // WT-327: a recurrence rule owns every occurrence's time, so a one-off ScheduledAt on
        // the same request would have to be silently discarded — and a silently discarded field
        // on this exact dialog is the bug this feature exists to remove. Refuse instead.
        RuleFor(x => x.Recurrence)
            .Must((request, _) => !request.ScheduledAt.HasValue)
            .When(x => x.Recurrence is not null)
            .WithMessage(RecurrenceMessages.ScheduledAtWithRecurrence);

        // Shape only. The full rule — supported type, resolvable zone, terminating end date —
        // is RecurrencePlanner's, because it needs the clock and produces the defaults; a
        // second copy here would be a second thing to keep in step.
        RuleFor(x => x.Recurrence!.Type)
            .Must(type => RecurrenceTypes.Normalize(type) != null)
            .When(x => x.Recurrence is not null)
            .WithMessage(RecurrenceMessages.TypeUnrecognised);

        RuleFor(x => x.Recurrence!.StartTimeLocal)
            .NotEmpty()
            .When(x => x.Recurrence is not null)
            .WithMessage(RecurrenceMessages.TimeMalformed);

        RuleFor(x => x.Recurrence!.TimeZone)
            .NotEmpty()
            .When(x => x.Recurrence is not null)
            .WithMessage(RecurrenceMessages.TimeZoneUnknown);
    }
}
