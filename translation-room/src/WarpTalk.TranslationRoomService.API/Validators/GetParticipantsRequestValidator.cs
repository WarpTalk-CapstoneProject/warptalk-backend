using System;
using System.Linq;
using FluentValidation;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.Validators;

public class GetParticipantsRequestValidator : AbstractValidator<GetParticipantsRequest>
{
    public GetParticipantsRequestValidator()
    {
        RuleFor(x => x.Search)
            .MaximumLength(100).WithMessage(TranslationRoomConstants.ValidationSearchTermMaxLength);

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s) || Enum.TryParse<TranslationRoomParticipantStatus>(s, true, out _))
            .WithMessage(TranslationRoomConstants.ValidationInvalidParticipantStatus);

        RuleFor(x => x.Role)
            .Must(r => string.IsNullOrEmpty(r) || Enum.TryParse<TranslationRoomParticipantRole>(r, true, out _))
            .WithMessage(TranslationRoomConstants.ValidationInvalidParticipantRole);

        RuleFor(x => x.SortBy)
            .Must(s => string.IsNullOrEmpty(s) || new[] { "displayname", "status", "role", "joinedat" }.Contains(s.ToLower()))
            .WithMessage(TranslationRoomConstants.ValidationInvalidSortBy);
    }
}
