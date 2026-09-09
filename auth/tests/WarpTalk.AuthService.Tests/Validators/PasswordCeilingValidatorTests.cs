using System.Linq;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests.Validators;

/// <summary>
/// WT-649 added a password ceiling to login. That rule is only safe if every path that WRITES a
/// password enforces the same one — otherwise somebody sets a longer password through a route that
/// still allows it and can no longer sign in, with reset-password handing them straight back into
/// the same lockout. These tests are what keep the four paths agreeing.
/// </summary>
public class PasswordCeilingValidatorTests
{
    private static string OverTheLimit => new('a', UserConstants.PasswordMaxLength + 1);

    [Fact]
    public void RegisterInvited_ShouldFail_WhenPasswordIsOverTheLengthLimit()
    {
        var result = new RegisterInvitedRequestValidator()
            .Validate(new RegisterInvitedRequest("invite-token", OverTheLimit, "John Doe"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Password" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.PasswordMaxLength);
    }

    [Fact]
    public void RegisterInvited_ShouldFail_WhenFullNameIsOverTheLengthLimit()
    {
        var result = new RegisterInvitedRequestValidator()
            .Validate(new RegisterInvitedRequest("invite-token", "password123", new string('a', UserConstants.FullNameMaxLength + 1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "FullName" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.FullNameMaxLength);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("1234_abc")]
    public void RegisterInvited_ShouldFail_WhenLanguageTagsAreNotBcp47(string languageTag)
    {
        // The invited path carries the same two language fields as self-registration and used to
        // validate neither, so it was the one way free text reached the column.
        var result = new RegisterInvitedRequestValidator()
            .Validate(new RegisterInvitedRequest("invite-token", "password123", "John Doe", languageTag, languageTag));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "DefaultSpeakLanguage" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.LanguageTagInvalid);
        Assert.Contains(result.Errors, e => e.PropertyName == "DefaultListenLanguage" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.LanguageTagInvalid);
    }

    [Fact]
    public void ChangePassword_ShouldFail_WhenNewPasswordIsOverTheLengthLimit()
    {
        var result = new ChangePasswordRequestValidator()
            .Validate(new ChangePasswordRequest("current-password", OverTheLimit));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "NewPassword" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.NewPasswordMaxLength);
    }

    [Fact]
    public void ResetPassword_ShouldFail_WhenNewPasswordIsOverTheLengthLimit()
    {
        var result = new ResetPasswordRequestValidator()
            .Validate(new ResetPasswordRequest("reset-token", OverTheLimit));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "NewPassword" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.NewPasswordMaxLength);
    }

    [Fact]
    public void ResetPassword_ShouldFail_WhenRequiredFieldsAreEmpty()
    {
        // This endpoint had no validator at all before WT-649.
        var result = new ResetPasswordRequestValidator().Validate(new ResetPasswordRequest("", ""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Token");
        Assert.Contains(result.Errors, e => e.PropertyName == "NewPassword" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.NewPasswordRequired);
    }

    [Fact]
    public void ResetPassword_ShouldPass_WhenTheNewPasswordSitsInsideBothBounds()
    {
        var result = new ResetPasswordRequestValidator()
            .Validate(new ResetPasswordRequest("reset-token", new string('a', UserConstants.PasswordMaxLength)));

        Assert.True(result.IsValid);
    }
}
