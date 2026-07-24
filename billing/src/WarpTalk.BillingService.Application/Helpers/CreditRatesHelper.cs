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
    //Calculate the cost of the reservation based on the audio duration and the rates
    public static int CalculateReservationCost(ReservationCostRequest request)
    {
        double ratePerMinute = request.IsVoiceClone
            ? request.VoiceCloneRateMin
            : request.SttRateMin + request.TranslationRateMin + request.TtsRateMin;
        return (int)Math.Max(1, Math.Ceiling((request.AudioSeconds / HelperConstants.CreditRates.Rates.SecondsPerMinute) * ratePerMinute));
    }

    public static int CalculateMeetingReservationCost(int participantCount, string mediaStreamType, double sttRateSec)
    {
        // 1. Resolve Rate Meeting (Credit / Part-Min) from Section 4:
        // Opus Audio-Only: 0.2 Credit / Part-Min
        // Video SD: 0.5 Credit / Part-Min
        // Video HD: 1.0 Credit / Part-Min
        double rateMeeting = mediaStreamType.ToLowerInvariant() switch
        {
            HelperConstants.CreditRates.MediaStreamTypes.Audio => HelperConstants.CreditRates.Rates.Audio,
            HelperConstants.CreditRates.MediaStreamTypes.VideoSd => HelperConstants.CreditRates.Rates.VideoSd,
            HelperConstants.CreditRates.MediaStreamTypes.VideoHd => HelperConstants.CreditRates.Rates.VideoHd,
            _ => HelperConstants.CreditRates.Rates.DefaultVideoSd // Default to Video SD
        };

        // 2. STT basic rate per minute (Credit / Part-Min)
        // From plan: 1.0 Credit / Second of audio = 60.0 Credits / Minute
        double rateSttBasic = sttRateSec * HelperConstants.CreditRates.Rates.SecondsPerMinute;

        // 3. Compute for 15 minutes block
        double totalRatePerPartMin = rateMeeting + rateSttBasic;
        double cost = participantCount * HelperConstants.CreditRates.Rates.BlockMinutes * totalRatePerPartMin;

        return (int)Math.Max(1, Math.Ceiling(cost));
    }

    /// <summary>
    /// Calculates the credit cost for a mixed-service usage event using configurable rates.
    /// </summary>
    public static int CalculateCreditCost(CreditCostRequest request)
    {
        double cost = request.AudioSeconds * request.Rates.SttPerSecond;
        cost += (request.TokenCount / 100.0) * request.Rates.TranslationPer100Chars;

        double ttsSeconds = request.GpuInferenceMs / 1000.0;
        double ttsRate = request.IsVoiceClone ? request.Rates.VoiceClonePerSecond : request.Rates.StandardTtsPerSecond;
        cost += ttsSeconds * ttsRate;

        if (cost <= 0 && (request.AudioSeconds > 0 || request.TokenCount > 0 || request.GpuInferenceMs > 0))
            return 1;

        return (int)Math.Max(1, Math.Ceiling(cost));
    }
}
