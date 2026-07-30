using WarpTalk.TranslationRoomService.API.Validators;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using Xunit;
using FluentValidation.TestHelper;

namespace WarpTalk.TranslationRoomService.Tests.API.Validators;

public class JoinTranslationRoomRequestValidatorTests
{
    private readonly JoinTranslationRoomRequestValidator _validator;

    public JoinTranslationRoomRequestValidatorTests()
    {
        _validator = new JoinTranslationRoomRequestValidator();
    }

    [Fact]
    public void Should_Have_Error_When_Code_Is_Null()
    {
        var request = new JoinTranslationRoomRequest(null!, "Test User", "en", "vi");
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.TranslationRoomCode)
            .WithErrorMessage(TranslationRoomConstants.ValidationTranslationRoomCodeRequired);
    }

    [Fact]
    public void Should_Have_Error_When_Code_Contains_Digits()
    {
        var request = new JoinTranslationRoomRequest("ab1-def2-hi3", "Test User", "en", "vi");
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.TranslationRoomCode)
            .WithErrorMessage(TranslationRoomConstants.ValidationTranslationRoomCodeFormat);
    }

    [Fact]
    public void Should_Have_Error_When_DisplayName_Is_Null()
    {
        var request = new JoinTranslationRoomRequest("abc-defg-hij", null!, "en", "vi");
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.DisplayName)
            .WithErrorMessage(TranslationRoomConstants.ValidationDisplayNameRequired);
    }

    [Fact]
    public void Should_Not_Have_Error_When_Request_Is_Valid()
    {
        var request = new JoinTranslationRoomRequest("abc-defg-hij", "Test User", "en", "vi");
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
