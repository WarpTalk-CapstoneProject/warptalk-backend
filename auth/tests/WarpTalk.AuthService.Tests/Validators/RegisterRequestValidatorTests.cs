using System.Linq;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests.Validators;

public class RegisterRequestValidatorTests
{
    private readonly RegisterRequestValidator _validator;

    public RegisterRequestValidatorTests()
    {
        _validator = new RegisterRequestValidator();
    }

    [Theory]
    [InlineData("test.user@gmail.com")]
    [InlineData("TEST@GMAIL.COM")]
    [InlineData("Someone.Else+Label@Gmail.com")]
    [InlineData("test.user@yahoo.com")]
    [InlineData("test.user@warptalk.vn")]
    [InlineData("test@gmail.co")]
    public void Validate_ShouldPass_WhenEmailIsValid(string email)
    {
        // Arrange
        var request = new RegisterRequest(email, "password123", "John Doe");

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
        var request = new RegisterRequest(email, "password123", "John Doe");

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
        var request = new RegisterRequest("", "", "");

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.EmailRequired);
        Assert.Contains(result.Errors, e => e.PropertyName == "Password" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.PasswordRequired);
        Assert.Contains(result.Errors, e => e.PropertyName == "FullName" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.FullNameRequired);
    }
}
