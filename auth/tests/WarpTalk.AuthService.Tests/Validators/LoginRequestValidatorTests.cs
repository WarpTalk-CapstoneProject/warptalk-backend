using System.Linq;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests.Validators;

public class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _validator;

    public LoginRequestValidatorTests()
    {
        _validator = new LoginRequestValidator();
    }

    [Theory]
    [InlineData("test.user@gmail.com")]
    [InlineData("TEST@GMAIL.COM")]
    [InlineData("Someone.Else+Label@Gmail.com")]
    [InlineData("test.user@yahoo.com")]
    [InlineData("test.user@warptalk.vn")]
    public void Validate_ShouldPass_WhenEmailIsValid(string email)
    {
        // Arrange
        var request = new LoginRequest(email, "password123", null, null);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("test@invalid")]
    [InlineData("@domain.com")]
    public void Validate_ShouldFail_WhenEmailIsInvalid(string email)
    {
        // Arrange
        var request = new LoginRequest(email, "password123", null, null);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        var emailError = result.Errors.FirstOrDefault(e => e.PropertyName == "Email");
        Assert.NotNull(emailError);
        Assert.Equal(ApiMessageConstants.ValidationMessages.EmailInvalidFormat, emailError.ErrorMessage);
    }

    [Fact]
    public void Validate_ShouldFail_WhenRequiredFieldsAreEmpty()
    {
        // Arrange
        var request = new LoginRequest("", "", null, null);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.EmailRequired);
        Assert.Contains(result.Errors, e => e.PropertyName == "Password" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.PasswordRequired);
    }

    /// <summary>
    /// The 10,000-character case is the point of this test. Login is unauthenticated, so whatever
    /// it accepts is what an anonymous caller can make the server hold and hash on demand; the
    /// rule has to reject it outright rather than let it through to the hasher.
    /// </summary>
    [Fact]
    public void Validate_ShouldFail_WhenPasswordIsOverTheLengthLimit()
    {
        // Arrange
        var justOver = new string('a', UserConstants.PasswordMaxLength + 1);
        var absurd = new string('a', 10_000);

        // Act
        var justOverResult = _validator.Validate(new LoginRequest("test.user@gmail.com", justOver, null, null));
        var absurdResult = _validator.Validate(new LoginRequest("test.user@gmail.com", absurd, null, null));

        // Assert
        Assert.False(justOverResult.IsValid);
        Assert.Contains(justOverResult.Errors, e => e.PropertyName == "Password" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.PasswordMaxLength);
        Assert.False(absurdResult.IsValid);
        Assert.Contains(absurdResult.Errors, e => e.PropertyName == "Password" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.PasswordMaxLength);
    }

    [Fact]
    public void Validate_ShouldFail_WhenEmailIsOverTheLengthLimit()
    {
        // Arrange
        const string domain = "@example.com";
        var email = new string('a', UserConstants.EmailMaxLength + 1 - domain.Length) + domain;

        // Act
        var result = _validator.Validate(new LoginRequest(email, "password123", null, null));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.EmailMaxLength);
    }
}
