using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class InviteMemberRequestValidator : AbstractValidator<InviteMemberRequest>
{
    public InviteMemberRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.EmailRequired)
            .Matches(UserConstants.PermittedEmailRegex).WithMessage(ApiMessageConstants.ValidationMessages.EmailInvalidFormat);

        RuleFor(x => x.RoleName)
            .NotEmpty().WithMessage("Role name is required.");
    }
}
