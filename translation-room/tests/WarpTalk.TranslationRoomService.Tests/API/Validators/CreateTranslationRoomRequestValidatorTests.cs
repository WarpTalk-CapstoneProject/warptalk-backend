using FluentValidation.TestHelper;
using WarpTalk.TranslationRoomService.API.Validators;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.Tests.API.Validators;

public class CreateTranslationRoomRequestValidatorTests
{
    private readonly CreateTranslationRoomRequestValidator _validator;

    public CreateTranslationRoomRequestValidatorTests()
    {
        _validator = new CreateTranslationRoomRequestValidator();
    }

    [Fact]
    public void Should_Have_Error_When_Title_Is_Null()
    {
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), null!, "Description", WarpTalk.TranslationRoomService.Domain.Enums.TranslationRoomType.INSTANT, 10, "vi", new List<string> { "en" }, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Title)
              .WithErrorMessage(ApiMessageConstants.ValidationMessages.TitleRequired);
    }

    [Fact]
    public void Should_Have_Error_When_Title_Exceeds_MaxLength()
    {
        var longTitle = new string('A', 256);
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), longTitle, "Description", WarpTalk.TranslationRoomService.Domain.Enums.TranslationRoomType.INSTANT, 10, "vi", new List<string> { "en" }, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Title)
              .WithErrorMessage(ApiMessageConstants.ValidationMessages.TitleMaxLength);
    }

    [Fact]
    public void Should_Have_Error_When_SourceLanguage_Is_Null()
    {
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), "Valid Title", "Description", WarpTalk.TranslationRoomService.Domain.Enums.TranslationRoomType.INSTANT, 10, null!, new List<string> { "en" }, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.SourceLanguage)
              .WithErrorMessage(TranslationRoomConstants.ValidationSourceLanguageRequired);
    }

    [Fact]
    public void Should_Have_Error_When_TargetLanguages_Is_Null()
    {
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), "Valid Title", "Description", WarpTalk.TranslationRoomService.Domain.Enums.TranslationRoomType.INSTANT, 10, "vi", null!, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.TargetLanguages)
              .WithErrorMessage(TranslationRoomConstants.ValidationTargetLanguagesRequired);
    }

    [Fact]
    public void Should_Have_Error_When_MaxParticipants_Is_Zero()
    {
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), "Valid Title", "Description", WarpTalk.TranslationRoomService.Domain.Enums.TranslationRoomType.INSTANT, 0, "vi", new List<string> { "en" }, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.MaxParticipants)
              .WithErrorMessage(TranslationRoomConstants.ValidationMaxParticipantsGreaterThanZero);
    }

    [Fact]
    public void Should_Have_Error_When_ScheduledAt_Is_In_The_Past()
    {
        var pastDate = DateTime.UtcNow.AddMinutes(-5);
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), "Valid Title", "Description", WarpTalk.TranslationRoomService.Domain.Enums.TranslationRoomType.SCHEDULED, 10, "vi", new List<string> { "en" }, null, pastDate);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.ScheduledAt)
              .WithErrorMessage(TranslationRoomConstants.ValidationScheduledTimeMustBeFuture);
    }
}
