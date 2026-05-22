using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.EmailRequired)
            .EmailAddress().WithMessage(ApiMessageConstants.ValidationMessages.EmailInvalidFormat);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.PasswordRequired)
            .MinimumLength(6).WithMessage(ApiMessageConstants.ValidationMessages.PasswordMinLength);

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.FullNameRequired);
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.EmailRequired)
            .EmailAddress().WithMessage(ApiMessageConstants.ValidationMessages.EmailInvalidFormat);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.PasswordRequired);
    }
}

public class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.RefreshTokenRequired);
    }
}

public class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty().WithMessage(ApiMessageConstants.ValidationMessages.RefreshTokenRequired);
    }
}
