using System.Linq;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests.Validators;

public class InviteMemberRequestValidatorTests
{
    private readonly InviteMemberRequestValidator _validator;

    public InviteMemberRequestValidatorTests()
    {
        _validator = new InviteMemberRequestValidator();
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
        var request = new InviteMemberRequest(email, "Member");

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
        var request = new InviteMemberRequest(email, "Member");

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
        var request = new InviteMemberRequest("", "");

        // Act
        var result = _validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email" && e.ErrorMessage == ApiMessageConstants.ValidationMessages.EmailRequired);
        Assert.Contains(result.Errors, e => e.PropertyName == "RoleName" && e.ErrorMessage == "Role name is required.");
    }
}
