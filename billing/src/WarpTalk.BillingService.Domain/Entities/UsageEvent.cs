// =======================================================
// Domain/Entities/UsageEvent.cs
// =======================================================

using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class UsageEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid? MeetingId { get; set; }
    public Guid? MeetingUsageSessionId { get; set; }

    public Guid SpeakerUserId { get; set; }

    public BillingFeatureType FeatureType { get; set; }

    /// <summary>
    /// Original spoken language
    /// </summary>
    public string SourceLanguage { get; set; }
        = string.Empty;

    /// <summary>
    /// Generated target language
    /// </summary>
    public string TargetLanguage { get; set; }
        = string.Empty;

    /// <summary>
    /// Audio duration or processing duration
    /// </summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// Number of translated language streams
    /// </summary>
    public int TargetLanguageCount { get; set; }

    public bool IsVoiceCloningEnabled { get; set; }

    /// <summary>
    /// Credits calculated by pricing engine
    /// </summary>
    public decimal CalculatedCredits { get; set; }

    public UsageEventStatus Status { get; set; }
        = UsageEventStatus.Pending;

    /// <summary>
    /// Prevent duplicate usage processing
    /// </summary>
    public string IdempotencyKey { get; set; }
        = string.Empty;

    public DateTime CreatedAt { get; set; }
        = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

}