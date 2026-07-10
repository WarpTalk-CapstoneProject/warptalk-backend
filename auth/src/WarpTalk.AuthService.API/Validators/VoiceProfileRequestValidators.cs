using FluentValidation;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Validators;

public class CreateVoiceProfileRequestValidator : AbstractValidator<CreateVoiceProfileRequest>
{
    public CreateVoiceProfileRequestValidator()
    {
        RuleFor(x => x.DisplayName)
            .MaximumLength(VoiceProfileConstants.MaxDisplayNameLength)
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceDisplayNameMaxLength)
            .When(x => x.DisplayName != null);

        RuleFor(x => x.Provider)
            .MaximumLength(VoiceProfileConstants.MaxProviderLength)
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceProviderMaxLength)
            .When(x => x.Provider != null);
    }
}

public class UpdateVoiceProfileRequestValidator : AbstractValidator<UpdateVoiceProfileRequest>
{
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        VoiceProfileConstants.StatusDraft,
        VoiceProfileConstants.StatusPendingConsent,
        VoiceProfileConstants.StatusTraining,
        VoiceProfileConstants.StatusReady,
        VoiceProfileConstants.StatusFailed,
        VoiceProfileConstants.StatusDisabled
    };

    public UpdateVoiceProfileRequestValidator()
    {
        RuleFor(x => x.DisplayName)
            .MaximumLength(VoiceProfileConstants.MaxDisplayNameLength)
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceDisplayNameMaxLength)
            .When(x => x.DisplayName != null);

        RuleFor(x => x.Provider)
            .MaximumLength(VoiceProfileConstants.MaxProviderLength)
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceProviderMaxLength)
            .When(x => x.Provider != null);

        RuleFor(x => x.EmbeddingRef)
            .MaximumLength(VoiceProfileConstants.MaxReferenceLength)
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceReferenceMaxLength)
            .When(x => x.EmbeddingRef != null);

        RuleFor(x => x.Status)
            .Must(status => status != null && ValidStatuses.Contains(status))
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceStatusInvalid)
            .When(x => x.Status != null);
    }
}

public class AddVoiceSampleRequestValidator : AbstractValidator<AddVoiceSampleRequest>
{
    private static readonly HashSet<string> ValidSampleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        VoiceProfileConstants.SampleTypeUploaded,
        VoiceProfileConstants.SampleTypeTraining,
        VoiceProfileConstants.SampleTypeVerification
    };

    public AddVoiceSampleRequestValidator()
    {
        RuleFor(x => x.SampleType)
            .Must(sampleType => !string.IsNullOrWhiteSpace(sampleType) && ValidSampleTypes.Contains(sampleType))
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceSampleTypeInvalid);

        RuleFor(x => x.FileUrl)
            .NotEmpty()
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceSampleFileUrlRequired)
            .MaximumLength(VoiceProfileConstants.MaxReferenceLength)
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceReferenceMaxLength);

        RuleFor(x => x.DurationSeconds)
            .InclusiveBetween(VoiceProfileConstants.MinSampleDurationSeconds, VoiceProfileConstants.MaxSampleDurationSeconds)
            .WithMessage(string.Format(
                ApiMessageConstants.ValidationMessages.VoiceSampleDurationOutOfBounds,
                VoiceProfileConstants.MinSampleDurationSeconds,
                VoiceProfileConstants.MaxSampleDurationSeconds))
            .When(x => x.DurationSeconds.HasValue);

        RuleFor(x => x.Language)
            .Matches(UserConstants.LanguageCodeRegex)
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceSampleLanguageInvalid)
            .When(x => x.Language != null);
    }
}

public class GrantVoiceConsentRequestValidator : AbstractValidator<GrantVoiceConsentRequest>
{
    public GrantVoiceConsentRequestValidator()
    {
        RuleFor(x => x.ConsentType)
            .Must(type => string.Equals(type, VoiceProfileConstants.ConsentTypeVoiceClone, StringComparison.OrdinalIgnoreCase))
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceConsentTypeInvalid);

        RuleFor(x => x.ConsentTextVersion)
            .NotEmpty()
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceConsentTextVersionRequired)
            .MaximumLength(VoiceProfileConstants.MaxConsentTextVersionLength);
    }
}

public class RevokeVoiceConsentRequestValidator : AbstractValidator<RevokeVoiceConsentRequest>
{
    public RevokeVoiceConsentRequestValidator()
    {
        RuleFor(x => x.ConsentType)
            .Must(type => string.Equals(type, VoiceProfileConstants.ConsentTypeVoiceClone, StringComparison.OrdinalIgnoreCase))
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceConsentTypeInvalid);

        RuleFor(x => x.ConsentTextVersion)
            .NotEmpty()
            .WithMessage(ApiMessageConstants.ValidationMessages.VoiceConsentTextVersionRequired)
            .MaximumLength(VoiceProfileConstants.MaxConsentTextVersionLength);
    }
}
