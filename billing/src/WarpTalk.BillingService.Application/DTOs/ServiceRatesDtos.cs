namespace WarpTalk.BillingService.Application.DTOs;

public record ServiceRatesDto(
    double SttPerSecond,
    double TranslationPer100Chars,
    double StandardTtsPerSecond,
    double VoiceClonePerSecond,
    double AiAssistantInputPer1000Tokens,
    double AiAssistantOutputPer1000Tokens
);


public record UpdateServiceRatesRequest(
    double SttPerSecond,
    double TranslationPer100Chars,
    double StandardTtsPerSecond,
    double VoiceClonePerSecond,
    double AiAssistantInputPer1000Tokens,
    double AiAssistantOutputPer1000Tokens
);
