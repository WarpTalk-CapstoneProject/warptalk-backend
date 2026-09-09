using System.Linq;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
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

    [Theory]
    [InlineData(UserConstants.PasswordMinLength - 1, ApiMessageConstants.ValidationMessages.PasswordMinLength)]
    [InlineData(UserConstants.PasswordMaxLength + 1, ApiMessageConstants.ValidationMessages.PasswordMaxLength)]
    public void Validate_ShouldFail_WhenPasswordIsOutsideTheAllowedLength(int length, string expectedMessage)
    {
        // Arrange
        var request = new RegisterRequest("test.user@gmail.com", new string('a', length), "John Doe");

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Password" && e.ErrorMessage == expectedMessage);
    }

    public static TheoryData<string, string> InvalidFullNames() => new()
    {
        // Whitespace is already caught: FluentValidation's NotEmpty routes strings through
        // String.IsNullOrWhiteSpace. Pinned here so nobody "optimises" that away.
        { " ", ApiMessageConstants.ValidationMessages.FullNameRequired },
        { new string('a', UserConstants.FullNameMaxLength + 1), ApiMessageConstants.ValidationMessages.FullNameMaxLength },
    };

    /// <summary>
    /// WT-649: a name one character past the column width used to reach SaveChangesAsync and come
    /// back as an unexplained server error. It has to fail here, before any of that.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidFullNames))]
    public void Validate_ShouldFail_WhenFullNameIsBlankOrTooLong(string fullName, string expectedMessage)
    {
        // Arrange
        var request = new RegisterRequest("test.user@gmail.com", "password123", fullName);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "FullName" && e.ErrorMessage == expectedMessage);
    }

    [Fact]
    public void Validate_ShouldFail_WhenEmailIsWellFormedButOverTheLengthLimit()
    {
        // Arrange — well formed on purpose, so the only thing that can reject it is the ceiling.
        const string domain = "@example.com";
        var email = new string('a', UserConstants.EmailMaxLength + 1 - domain.Length) + domain;
        var request = new RegisterRequest(email, "password123", "John Doe");

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.Equal(UserConstants.EmailMaxLength + 1, email.Length);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.EmailMaxLength);
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en-US")]
    [InlineData("zh-Hans-CN")]
    public void Validate_ShouldPass_WhenLanguageTagsAreValid(string languageTag)
    {
        // Arrange
        var request = new RegisterRequest("test.user@gmail.com", "password123", "John Doe", languageTag, languageTag);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("v")]
    [InlineData("1234_abc")]
    public void Validate_ShouldFail_WhenLanguageTagsAreNotBcp47(string languageTag)
    {
        // Arrange
        var request = new RegisterRequest("test.user@gmail.com", "password123", "John Doe", languageTag, languageTag);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "DefaultSpeakLanguage" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.LanguageTagInvalid);
        Assert.Contains(result.Errors, e => e.PropertyName == "DefaultListenLanguage" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.LanguageTagInvalid);
    }
}
