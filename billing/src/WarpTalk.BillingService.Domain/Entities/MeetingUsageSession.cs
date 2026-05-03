// =======================================================
// Domain/Entities/MeetingUsageSession.cs
// =======================================================

using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class MeetingUsageSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid MeetingId { get; set; }

    public Guid HostUserId { get; set; }

    public bool IsVoiceMode { get; set; }

    public int ActiveParticipants { get; set; }

    /// <summary>
    /// Number of active translated streams
    /// </summary>
    public int ActiveLanguageStreams { get; set; }

    /// <summary>
    /// Realtime estimated usage for UI/dashboard
    /// </summary>
    public decimal EstimatedCreditsConsumed { get; set; }

    public QuotaMode QuotaMode { get; set; }
        = QuotaMode.FullVoice;

    public UsageSessionStatus Status { get; set; }
        = UsageSessionStatus.Active;

    public DateTime StartedAt { get; set; }
        = DateTime.UtcNow;

    public DateTime? EndedAt { get; set; }
}