namespace WarpTalk.AuthService.Domain.Entities;

public class VoiceSample
{
    public Guid Id { get; set; }

    public Guid VoiceProfileId { get; set; }

    public string SampleType { get; set; } = null!;

    public string? FileUrl { get; set; }

    public int? DurationSeconds { get; set; }

    public string? Language { get; set; }

    public bool ContainsRawAudio { get; set; }

    public DateTime? RetentionUntil { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public virtual VoiceProfile VoiceProfile { get; set; } = null!;
}
