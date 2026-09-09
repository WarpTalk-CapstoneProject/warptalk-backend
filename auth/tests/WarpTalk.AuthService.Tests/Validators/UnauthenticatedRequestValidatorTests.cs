using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests.Validators;

/// <summary>
/// The three endpoints that took a request body with no validator behind it at all. Every one of
/// them is reachable without a session, so whatever they accepted is what an anonymous caller
/// could make the server carry.
/// </summary>
public class UnauthenticatedRequestValidatorTests
{
    private static string AddressOfLength(int length)
    {
        const string domain = "@example.com";
        return new string('a', length - domain.Length) + domain;
    }

    private readonly ForgotPasswordRequestValidator _forgotPassword = new();
    private readonly ResendVerificationRequestValidator _resendVerification = new();
    private readonly VerifyEmailRequestValidator _verifyEmail = new();

    [Fact]
    public void ForgotPassword_ShouldFail_WhenEmailIsOverTheLengthLimit()
    {
        var result = _forgotPassword.Validate(
            new ForgotPasswordRequest(AddressOfLength(UserConstants.EmailMaxLength + 1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == ApiMessageConstants.ValidationMessages.EmailMaxLength);
    }

    [Fact]
    public void ForgotPassword_ShouldPass_ForAnOrdinaryAddress()
    {
        Assert.True(_forgotPassword.Validate(new ForgotPasswordRequest("someone@example.com")).IsValid);
    }

    /// <summary>
    /// The invariant worth protecting, and the reason these two validators are duplicated rather
    /// than one being made stricter: forgot-password and resend-verification answer identically so
    /// that neither can be used to discover which addresses have accounts. A difference in what
    /// they REFUSE is a difference in what they answer.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("someone@example.com")]
    [InlineData("Someone.Else+Label@Example.com")]
    public void ForgotPasswordAndResendVerification_ShouldAgree_OnEveryAddress(string email)
    {
        var forgot = _forgotPassword.Validate(new ForgotPasswordRequest(email));
        var resend = _resendVerification.Validate(new ResendVerificationRequest(email));

        Assert.Equal(forgot.IsValid, resend.IsValid);
        Assert.Equal(
            forgot.Errors.ConvertAll(e => e.ErrorMessage),
            resend.Errors.ConvertAll(e => e.ErrorMessage));
    }

    [Fact]
    public void ForgotPasswordAndResendVerification_ShouldAgree_OnAnOverLongAddress()
    {
        var email = AddressOfLength(UserConstants.EmailMaxLength + 1);

        var forgot = _forgotPassword.Validate(new ForgotPasswordRequest(email));
        var resend = _resendVerification.Validate(new ResendVerificationRequest(email));

        Assert.False(forgot.IsValid);
        Assert.Equal(forgot.IsValid, resend.IsValid);
        Assert.Equal(
            forgot.Errors.ConvertAll(e => e.ErrorMessage),
            resend.Errors.ConvertAll(e => e.ErrorMessage));
    }

    [Fact]
    public void VerifyEmail_ShouldFail_WhenTokenIsMissing()
    {
        var result = _verifyEmail.Validate(new VerifyEmailRequest(""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == ApiMessageConstants.ValidationMessages.TokenRequired);
    }

    [Fact]
    public void VerifyEmail_ShouldFail_WhenTokenIsAbsurdlyLong()
    {
        var result = _verifyEmail.Validate(new VerifyEmailRequest(new string('a', 10_000)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == ApiMessageConstants.ValidationMessages.TokenMaxLength);
    }

    [Fact]
    public void VerifyEmail_ShouldPass_ForATokenTheSystemActuallyIssues()
    {
        // TokenHashing.GenerateToken is 32 random bytes as base64url — 43 characters. The limit has
        // to be comfortably above what we hand out, or verification breaks for everybody.
        var issued = WarpTalk.AuthService.Application.Helpers.TokenHashing.GenerateToken();

        Assert.True(issued.Length < UserConstants.TokenMaxLength);
        Assert.True(_verifyEmail.Validate(new VerifyEmailRequest(issued)).IsValid);
    }
}
