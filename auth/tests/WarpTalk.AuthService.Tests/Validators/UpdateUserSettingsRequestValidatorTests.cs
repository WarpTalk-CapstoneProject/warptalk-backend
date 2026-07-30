using System.Linq;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using Xunit;

namespace WarpTalk.AuthService.Tests.Validators;

public class UpdateUserSettingsRequestValidatorTests
{
    private readonly UpdateUserSettingsRequestValidator _validator;

    public UpdateUserSettingsRequestValidatorTests()
    {
        _validator = new UpdateUserSettingsRequestValidator();
    }

    [Fact]
    public void Validate_ShouldPass_WhenRequestIsEmpty()
    {
        // Arrange
        var request = new UpdateUserSettingsRequest();

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(32)]
    public void Validate_ShouldPass_WhenFontSizeInBounds(int fontSize)
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(TranscriptFontSize: fontSize);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(33)]
    public void Validate_ShouldFail_WhenFontSizeOutOfBounds(int fontSize)
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(TranscriptFontSize: fontSize);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("TranscriptFontSize", error.PropertyName);
        Assert.Contains("Font size must be between", error.ErrorMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(250)]
    [InlineData(500)]
    public void Validate_ShouldPass_WhenMaxParticipantsInBounds(int maxParticipants)
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(DefaultMaxParticipants: maxParticipants);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void Validate_ShouldFail_WhenMaxParticipantsOutOfBounds(int maxParticipants)
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(DefaultMaxParticipants: maxParticipants);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("DefaultMaxParticipants", error.PropertyName);
        Assert.Contains("Default max participants must be between", error.ErrorMessage);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("DARK")]
    [InlineData("System")]
    public void Validate_ShouldPass_WhenThemeIsValid(string theme)
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(Theme: theme);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ShouldFail_WhenThemeIsInvalid()
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(Theme: "classic");

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Theme", error.PropertyName);
        Assert.Contains("Invalid theme. Supported", error.ErrorMessage);
    }

    [Theory]
    [InlineData("instant")]
    [InlineData("SCHEDULED")]
    [InlineData("Instant")]
    public void Validate_ShouldPass_WhenRoomTypeIsValid(string roomType)
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(DefaultTranslationRoomType: roomType);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ShouldFail_WhenRoomTypeIsInvalid()
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(DefaultTranslationRoomType: "invalid_type");

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("DefaultTranslationRoomType", error.PropertyName);
        Assert.Equal("Invalid translation room type.", error.ErrorMessage);
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void Validate_ShouldPass_WhenLanguageIsValid(string lang)
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(DefaultSpeakLanguage: lang, DefaultListenLanguage: lang);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("en-US-extra")]
    [InlineData("12-34")]
    public void Validate_ShouldFail_WhenLanguageIsInvalid(string lang)
    {
        // Arrange
        var request = new UpdateUserSettingsRequest(DefaultSpeakLanguage: lang);

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("DefaultSpeakLanguage", error.PropertyName);
        Assert.Equal("Invalid default speak language format.", error.ErrorMessage);
    }
}
