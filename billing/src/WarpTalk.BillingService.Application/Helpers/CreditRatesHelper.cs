using System;
using System.Collections.Generic;
using WarpTalk.BillingService.Domain.Constants;


namespace WarpTalk.BillingService.Application.Helpers;

public static class CreditRatesHelper
{
    private static readonly Dictionary<string, string> UsageTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Summary", UsageConstants.UsageTypes.Summary },
        { "VoiceCloning", UsageConstants.UsageTypes.VoiceCloning },
        { "Chat", UsageConstants.UsageTypes.Chat },
        { "TTS", UsageConstants.UsageTypes.TextToSpeech },
        { "STT", UsageConstants.UsageTypes.SpeechToText },
        { "AiSpeechTranslation", UsageConstants.UsageTypes.VoiceTranslation },
        { "Translation", UsageConstants.UsageTypes.VoiceTranslation }
    };

    public static string GetUsageType(string referenceType)
    {
        if (string.IsNullOrEmpty(referenceType)) return UsageConstants.UsageTypes.VoiceTranslation;
        return UsageTypeMap.TryGetValue(referenceType, out var usageType) ? usageType : UsageConstants.UsageTypes.VoiceTranslation;
    }
    //Calculate the cost of the reservation based on the audio duration and the rates
    public static int CalculateReservationCost(double audioSeconds, bool isVoiceClone, double sttRateMin, double transRateMin, double ttsRateMin, double vcRateMin)
    {
        double ratePerMinute = isVoiceClone ? vcRateMin : (sttRateMin + transRateMin + ttsRateMin);
        return (int)Math.Max(1, Math.Ceiling((audioSeconds / 60.0) * ratePerMinute));
    }

    public static int CalculateMeetingReservationCost(int participantCount, string mediaStreamType, double sttRateSec)
    {
        // 1. Resolve Rate Meeting (Credit / Part-Min) from Section 4:
        // Opus Audio-Only: 0.2 Credit / Part-Min
        // Video SD: 0.5 Credit / Part-Min
        // Video HD: 1.0 Credit / Part-Min
        double rateMeeting = mediaStreamType.ToLowerInvariant() switch
        {
            "audio" => 0.2,
            "video_sd" => 0.5,
            "video_hd" => 1.0,
            _ => 0.5 // Default to Video SD
        };

        // 2. STT basic rate per minute (Credit / Part-Min)
        // From plan: 1.0 Credit / Second of audio = 60.0 Credits / Minute
        double rateSttBasic = sttRateSec * 60.0;

        // 3. Compute for 15 minutes block
        double totalRatePerPartMin = rateMeeting + rateSttBasic;
        double cost = participantCount * 15.0 * totalRatePerPartMin;

        return (int)Math.Max(1, Math.Ceiling(cost));
    }
}
