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
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), null!, "Description", "INSTANT", 10, "vi", new List<string> { "en" }, null, null, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Title)
              .WithErrorMessage(ApiMessageConstants.ValidationMessages.TitleRequired);
    }

    [Fact]
    public void Should_Have_Error_When_WorkspaceId_Is_Empty()
    {
        var model = new CreateTranslationRoomRequest(Guid.Empty, "Valid Title", "Description", "INSTANT", 10, "vi", new List<string> { "en" }, null, null, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.WorkspaceId)
              .WithErrorMessage(ApiMessageConstants.ValidationMessages.WorkspaceRequired);
    }

    [Fact]
    public void Should_Have_Error_When_Title_Exceeds_MaxLength()
    {
        var longTitle = new string('A', 256);
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), longTitle, "Description", "INSTANT", 10, "vi", new List<string> { "en" }, null, null, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Title)
              .WithErrorMessage(ApiMessageConstants.ValidationMessages.TitleMaxLength);
    }

    [Fact]
    public void Should_Have_Error_When_SourceLanguage_Is_Null()
    {
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), "Valid Title", "Description", "INSTANT", 10, null!, new List<string> { "en" }, null, null, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.SourceLanguage)
              .WithErrorMessage(TranslationRoomConstants.ValidationSourceLanguageRequired);
    }

    [Fact]
    public void Should_Have_Error_When_TargetLanguages_Is_Null()
    {
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), "Valid Title", "Description", "INSTANT", 10, "vi", null!, null, null, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.TargetLanguages)
              .WithErrorMessage(TranslationRoomConstants.ValidationTargetLanguagesRequired);
    }

    [Fact]
    public void Should_Have_Error_When_MaxParticipants_Is_Zero()
    {
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), "Valid Title", "Description", "INSTANT", 0, "vi", new List<string> { "en" }, null, null, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.MaxParticipants)
              .WithErrorMessage(TranslationRoomConstants.ValidationMaxParticipantsGreaterThanZero);
    }

    [Fact]
    public void Should_Have_Error_When_ScheduledAt_Is_In_The_Past()
    {
        var pastDate = DateTime.UtcNow.AddMinutes(-5);
        var model = new CreateTranslationRoomRequest(Guid.NewGuid(), "Valid Title", "Description", "SCHEDULED", 10, "vi", new List<string> { "en" }, null, pastDate, null);
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.ScheduledAt)
              .WithErrorMessage(TranslationRoomConstants.ValidationScheduledTimeMustBeFuture);
    }

    [Fact]
    public void Should_Have_Error_When_External_Metadata_Is_Used_On_Non_Bridge_Room()
    {
        var model = new CreateTranslationRoomRequest(
            Guid.NewGuid(),
            "Google Meet",
            "Description",
            TranslationRoomTypes.Event,
            10,
            "vi",
            new List<string> { "en" },
            null,
            DateTime.UtcNow.AddHours(1),
            null,
            ExternalProvider: TranslationRoomConstants.ExternalProviderGoogleMeet,
            ExternalMeetingUrl: "https://meet.google.com/abc-defg-hij");

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ExternalProvider)
            .WithErrorMessage(TranslationRoomConstants.ValidationExternalMeetingRequiresBridgeType);
    }

    [Fact]
    public void Should_Have_Error_When_An_External_Meeting_Url_Arrives_Without_A_Provider()
    {
        // The host allow-list used to hang off the provider, so omitting the provider skipped it
        // and any HTTPS URL became the room's Join button for every invitee.
        var model = new CreateTranslationRoomRequest(
            Guid.NewGuid(),
            "Google Meet",
            "Description",
            TranslationRoomTypes.ExternalBridge,
            2,
            "vi",
            new List<string> { "en" },
            null,
            DateTime.UtcNow.AddHours(1),
            null,
            ExternalMeetingUrl: "https://meet-google.attacker.test/abc-defg-hij");

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ExternalProvider)
            .WithErrorMessage(TranslationRoomConstants.ValidationExternalProviderRequired);
    }

    [Fact]
    public void Should_Have_Error_When_A_Provider_Arrives_Without_A_Meeting_Url()
    {
        var model = new CreateTranslationRoomRequest(
            Guid.NewGuid(),
            "Google Meet",
            "Description",
            TranslationRoomTypes.ExternalBridge,
            2,
            "vi",
            new List<string> { "en" },
            null,
            DateTime.UtcNow.AddHours(1),
            null,
            ExternalProvider: TranslationRoomConstants.ExternalProviderGoogleMeet);

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ExternalMeetingUrl)
            .WithErrorMessage(TranslationRoomConstants.ValidationExternalMeetingUrlRequired);
    }

    [Theory]
    [InlineData("http://meet.google.com/abc-defg-hij")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not-a-url")]
    public void Should_Have_Error_When_The_External_Meeting_Url_Is_Not_Absolute_Https(string url)
    {
        var model = new CreateTranslationRoomRequest(
            Guid.NewGuid(),
            "Google Meet",
            "Description",
            TranslationRoomTypes.ExternalBridge,
            2,
            "vi",
            new List<string> { "en" },
            null,
            DateTime.UtcNow.AddHours(1),
            null,
            ExternalProvider: TranslationRoomConstants.ExternalProviderGoogleMeet,
            ExternalMeetingUrl: url);

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ExternalMeetingUrl);
    }

    [Fact]
    public void Should_Have_Error_When_The_External_Calendar_Event_Id_Exceeds_The_Column()
    {
        var model = new CreateTranslationRoomRequest(
            Guid.NewGuid(),
            "Google Meet",
            "Description",
            TranslationRoomTypes.ExternalBridge,
            2,
            "vi",
            new List<string> { "en" },
            null,
            DateTime.UtcNow.AddHours(1),
            null,
            ExternalProvider: TranslationRoomConstants.ExternalProviderGoogleMeet,
            ExternalMeetingUrl: "https://meet.google.com/abc-defg-hij",
            ExternalCalendarEventId: new string('e', TranslationRoomConstants.ExternalCalendarEventIdMaxLength + 1));

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ExternalCalendarEventId)
            .WithErrorMessage(TranslationRoomConstants.ValidationExternalCalendarEventIdTooLong);
    }

    [Fact]
    public void Should_Accept_Google_Meet_Metadata_For_External_Bridge_Room()
    {
        var model = new CreateTranslationRoomRequest(
            Guid.NewGuid(),
            "Google Meet",
            "Description",
            TranslationRoomTypes.ExternalBridge,
            2,
            "vi",
            new List<string> { "en" },
            null,
            DateTime.UtcNow.AddHours(1),
            null,
            ExternalProvider: TranslationRoomConstants.ExternalProviderGoogleMeet,
            ExternalMeetingUrl: "https://meet.google.com/abc-defg-hij",
            ExternalCalendarEventId: "calendar-event-1",
            ExternalCalendarEventUrl: "https://calendar.google.com/calendar/event?eid=calendar-event-1");

        var result = _validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.ExternalProvider);
        result.ShouldNotHaveValidationErrorFor(x => x.ExternalMeetingUrl);
        result.ShouldNotHaveValidationErrorFor(x => x.ExternalCalendarEventUrl);
    }

    [Fact]
    public void Should_Have_Error_When_Google_Meet_Url_Is_Not_Google_Meet()
    {
        var model = new CreateTranslationRoomRequest(
            Guid.NewGuid(),
            "Google Meet",
            "Description",
            TranslationRoomTypes.ExternalBridge,
            2,
            "vi",
            new List<string> { "en" },
            null,
            DateTime.UtcNow.AddHours(1),
            null,
            ExternalProvider: TranslationRoomConstants.ExternalProviderGoogleMeet,
            ExternalMeetingUrl: "https://example.com/abc-defg-hij");

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ExternalMeetingUrl)
            .WithErrorMessage(TranslationRoomConstants.ValidationGoogleMeetUrlInvalid);
    }
}
