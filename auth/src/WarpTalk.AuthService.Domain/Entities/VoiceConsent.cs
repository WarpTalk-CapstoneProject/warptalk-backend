using System;
using WarpTalk.AuthService.Domain.Enums;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class VoiceConsent
{
    public Guid Id { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid UserId { get; set; }

    public Guid? VoiceProfileId { get; set; }

    public string ConsentType { get; set; } = null!;

    public ConsentStatus ConsentStatus { get; set; }

    public string ConsentTextVersion { get; set; } = null!;

    public DateTime? GrantedAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? ContractSnapshot { get; set; }

    public string? ContractHash { get; set; }

    public bool OwnVoiceConfirmed { get; set; }

    public bool AiUseConfirmed { get; set; }

    public bool SyntheticVoiceAcknowledged { get; set; }

    public bool NoImpersonationConfirmed { get; set; }

    public bool RetentionAcknowledged { get; set; }

    public virtual VoiceProfile? VoiceProfile { get; set; }
}
