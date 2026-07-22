namespace WarpTalk.BillingService.Application.DTOs;

public record ServiceRatesDto(
    double SttPerMinute,
    double TranslationPerMinute,
    double StandardTtsPerMinute,
    double VoiceClonePerMinute,
    double AiSummaryPerRequest,
    double AiChatPerRequest
);


public record UpdateServiceRatesRequest(
    double SttPerMinute,
    double TranslationPerMinute,
    double StandardTtsPerMinute,
    double VoiceClonePerMinute,
    double AiSummaryPerRequest,
    double AiChatPerRequest
);
