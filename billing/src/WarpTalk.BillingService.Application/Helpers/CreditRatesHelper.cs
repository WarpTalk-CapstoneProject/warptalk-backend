using System;
using System.Collections.Generic;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Helpers;

public static class CreditRatesHelper
{
    private static readonly Dictionary<string, string> UsageTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { HelperConstants.CreditRates.ReferenceTypes.Summary, UsageConstants.UsageTypes.Summary },
        { HelperConstants.CreditRates.ReferenceTypes.VoiceCloning, UsageConstants.UsageTypes.VoiceCloning },
        { HelperConstants.CreditRates.ReferenceTypes.Chat, UsageConstants.UsageTypes.Chat },
        { HelperConstants.CreditRates.ReferenceTypes.TTS, UsageConstants.UsageTypes.TextToSpeech },
        { HelperConstants.CreditRates.ReferenceTypes.STT, UsageConstants.UsageTypes.SpeechToText },
        { HelperConstants.CreditRates.ReferenceTypes.AiSpeechTranslation, UsageConstants.UsageTypes.VoiceTranslation },
        { HelperConstants.CreditRates.ReferenceTypes.Translation, UsageConstants.UsageTypes.VoiceTranslation }
    };

    public static string GetUsageType(string referenceType)
    {
        if (string.IsNullOrEmpty(referenceType)) return UsageConstants.UsageTypes.VoiceTranslation;
        return UsageTypeMap.TryGetValue(referenceType, out var usageType) ? usageType : UsageConstants.UsageTypes.VoiceTranslation;
    }
}
