namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Current AI service credit rates returned to the client.
/// </summary>
public record ServiceRatesDto(
    double SttPerMinute,
    double TranslationPerMinute,
    double StandardTtsPerMinute,
    double VoiceClonePerMinute,
    double AiSummaryPerRequest,
    double AiChatPerRequest
);
