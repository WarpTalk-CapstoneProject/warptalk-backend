// =======================================================
// Domain/Entities/SubscriptionPlan.cs
// =======================================================

using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class SubscriptionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public PlanType Type { get; set; }

    public decimal MonthlyPriceVnd { get; set; }

    public decimal IncludedCredits { get; set; }

    /// <summary>
    /// Credits consumed per hour in voice mode
    /// </summary>
    public decimal VoiceTranslationRatePerHour { get; set; }

    /// <summary>
    /// Credits consumed per hour in text-only mode
    /// </summary>
    public decimal TextTranslationRatePerHour { get; set; }

    /// <summary>
    /// Extra multiplier for voice cloning
    /// </summary>
    public decimal VoiceCloningMultiplier { get; set; }

    /// <summary>
    /// Multiplier for multilingual routing
    /// Example:
    /// EN -> VI + JA + KR
    /// = 3 language streams
    /// </summary>
    public decimal MultiLanguageStreamMultiplier { get; set; }

    /// <summary>
    /// AI assistant pricing multiplier
    /// </summary>
    public decimal AiAssistantMultiplier { get; set; }

    public int MaxParticipants { get; set; }

    public int MaxConcurrentMeetings { get; set; }

    /// <summary>
    /// Pro = 2
    /// Premium = unlimited
    /// </summary>
    public int MaxLanguagesPerMeeting { get; set; }

    public bool SupportsVoiceCloning { get; set; }

    public bool SupportsAiAssistant { get; set; }

    public bool SupportsEnterpriseGlossary { get; set; }

    public bool SupportsMultiLanguageRoom { get; set; }

    public bool SupportsCreditRollover { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
        = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}