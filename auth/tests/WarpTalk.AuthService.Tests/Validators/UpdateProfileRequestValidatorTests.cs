using System.Linq;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
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
        Assert.Contains("Mã IANA timezone không hợp lệ.", error.ErrorMessage);
    }
}
