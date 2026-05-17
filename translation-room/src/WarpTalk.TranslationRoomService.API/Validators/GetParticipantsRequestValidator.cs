using System;
using System.Linq;
using FluentValidation;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Enums;
<<<<<<< HEAD
using WarpTalk.TranslationRoomService.Domain.Constants;
=======
>>>>>>> 80e45ad1325ea4819c4e38a4a5b6fa5c95549e8d

namespace WarpTalk.TranslationRoomService.API.Validators;

public class GetParticipantsRequestValidator : AbstractValidator<GetParticipantsRequest>
{
    public GetParticipantsRequestValidator()
    {
        RuleFor(x => x.Search)
<<<<<<< HEAD
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
=======
            .MaximumLength(100).WithMessage("Search term cannot exceed 100 characters.");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s) || Enum.TryParse<TranslationRoomParticipantStatus>(s, true, out _))
            .WithMessage("Status must be a valid TranslationRoomParticipantStatus.");

        RuleFor(x => x.Role)
            .Must(r => string.IsNullOrEmpty(r) || Enum.TryParse<TranslationRoomParticipantRole>(r, true, out _))
            .WithMessage("Role must be a valid TranslationRoomParticipantRole.");

        RuleFor(x => x.SortBy)
            .Must(s => string.IsNullOrEmpty(s) || new[] { "displayname", "status", "role", "joinedat" }.Contains(s.ToLower()))
            .WithMessage("SortBy must be one of: displayname, status, role, joinedat.");
>>>>>>> 80e45ad1325ea4819c4e38a4a5b6fa5c95549e8d
    }
}
