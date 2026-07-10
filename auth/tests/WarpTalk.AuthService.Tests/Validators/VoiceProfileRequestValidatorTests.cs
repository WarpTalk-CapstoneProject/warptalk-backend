using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.Tests.Validators;

public class VoiceProfileRequestValidatorTests
{
    [Fact]
    public void CreateVoiceProfileRequest_ShouldPass_WhenPayloadIsValid()
    {
        var validator = new CreateVoiceProfileRequestValidator();
        var request = new CreateVoiceProfileRequest("Host neutral", null, "xtts-v2");

        var result = validator.Validate(request);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void UpdateVoiceProfileRequest_ShouldFail_WhenStatusIsInvalid()
    {
        var validator = new UpdateVoiceProfileRequestValidator();
        var request = new UpdateVoiceProfileRequest(Status: "active");

        var result = validator.Validate(request);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Status", error.PropertyName);
    }

    [Fact]
    public void AddVoiceSampleRequest_ShouldPass_ForUploadedWavReference()
    {
        var validator = new AddVoiceSampleRequestValidator();
        var request = new AddVoiceSampleRequest(
            SampleType: "uploaded",
            FileUrl: "https://storage.example.com/sample.wav",
            DurationSeconds: 45,
            Language: "vi-VN");

        var result = validator.Validate(request);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AddVoiceSampleRequest_ShouldFail_WhenLanguageIsInvalid()
    {
        var validator = new AddVoiceSampleRequestValidator();
        var request = new AddVoiceSampleRequest(
            SampleType: "uploaded",
            FileUrl: "https://storage.example.com/sample.wav",
            Language: "vietnamese");

        var result = validator.Validate(request);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Language", error.PropertyName);
    }

    [Fact]
    public void GrantVoiceConsentRequest_ShouldFail_WhenConsentTypeIsInvalid()
    {
        var validator = new GrantVoiceConsentRequestValidator();
        var request = new GrantVoiceConsentRequest("marketing", "voice-consent-v1");

        var result = validator.Validate(request);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("ConsentType", error.PropertyName);
    }
}
