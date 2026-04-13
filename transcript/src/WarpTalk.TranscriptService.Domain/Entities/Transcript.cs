namespace WarpTalk.TranscriptService.Domain.Entities;

public class Transcript
{
    public Guid Id { get; set; }
    public Guid TranslationRoomId { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = "recording";
    public string SourceLanguage { get; set; } = null!;
    public int TotalSegments { get; set; }
    public int TotalDurationMs { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? FinalizedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
