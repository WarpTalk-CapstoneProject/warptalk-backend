using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class LinkGoogleRequestValidator : AbstractValidator<LinkGoogleRequest>
{
    public LinkGoogleRequestValidator()
    {
        RuleFor(x => x.IdToken)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.GoogleIdTokenRequired);
    }
}
