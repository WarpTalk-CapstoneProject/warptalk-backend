using WarpTalk.AuthService.Domain.Enums;

namespace WarpTalk.AuthService.Domain.Entities;

public class VoiceConsent
{
    public Guid Id { get; set; }

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

    public virtual VoiceProfile? VoiceProfile { get; set; }
}
