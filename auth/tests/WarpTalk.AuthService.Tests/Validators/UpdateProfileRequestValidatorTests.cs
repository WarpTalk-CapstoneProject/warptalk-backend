using System.Linq;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests.Validators;

public class UpdateProfileRequestValidatorTests
{
    private readonly UpdateProfileRequestValidator _validator;

    public UpdateProfileRequestValidatorTests()
    {
        _validator = new UpdateProfileRequestValidator();
    }

    [Fact]
    public void Validate_ShouldPass_WhenRequestIsEmpty()
    {
        // Arrange
        var request = new UpdateProfileRequest(null, null, null, null);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("Asia/Ho_Chi_Minh")]
    [InlineData("America/New_York")]
    [InlineData("UTC")]
    [InlineData("Europe/London")]
    public void Validate_ShouldPass_WhenTimezoneIsValidIanaId(string timezone)
    {
        // Arrange
        var request = new UpdateProfileRequest(null, null, null, timezone);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("Invalid/Timezone")]
    [InlineData("SE Asia Standard Time")] // Windows ID, should fail because we require IANA
    [InlineData("Not_A_Timezone")]
    public void Validate_ShouldFail_WhenTimezoneIsInvalid(string timezone)
    {
        // Arrange
        var request = new UpdateProfileRequest(null, null, null, timezone);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Timezone", error.PropertyName);
        Assert.Contains("Timezone must be a valid IANA identifier.", error.ErrorMessage);
    }

    /// <summary>
    /// WT-649. Editing a profile writes the same varchar(150) column that registration does, so it
    /// had the same unguarded path to a database error — reachable by anyone with an account, not
    /// only at sign-up.
    /// </summary>
    [Fact]
    public void Validate_ShouldFail_WhenFullNameIsOverTheLengthLimit()
    {
        // Arrange
        var request = new UpdateProfileRequest(new string('a', UserConstants.FullNameMaxLength + 1), null, null, null);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "FullName" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.FullNameMaxLength);
    }

    [Fact]
    public void Validate_ShouldPass_WhenFullNameSitsExactlyOnTheLimit()
    {
        // Arrange
        var request = new UpdateProfileRequest(new string('a', UserConstants.FullNameMaxLength), null, null, null);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    /// <summary>
    /// The language rule here used to be UserConstants.LanguageCodeRegex — stricter than the one
    /// registration applied, so an account created with "zh-Hans-CN" could never save its own
    /// profile again. Both now read the same LanguageTagRegex.
    /// </summary>
    [Theory]
    [InlineData("vi")]
    [InlineData("en-US")]
    [InlineData("zh-Hans-CN")]
    public void Validate_ShouldPass_WhenPreferredLanguageIsAValidTag(string languageTag)
    {
        // Arrange
        var request = new UpdateProfileRequest(null, null, languageTag, null);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("v")]
    [InlineData("1234_abc")]
    public void Validate_ShouldFail_WhenPreferredLanguageIsNotBcp47(string languageTag)
    {
        // Arrange
        var request = new UpdateProfileRequest(null, null, languageTag, null);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("PreferredLanguage", error.PropertyName);
        Assert.Equal(ApiMessageConstants.ValidationMessages.PreferredLanguageInvalid, error.ErrorMessage);
    }
}
