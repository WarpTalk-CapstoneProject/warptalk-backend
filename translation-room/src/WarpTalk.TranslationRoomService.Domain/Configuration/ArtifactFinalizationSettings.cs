namespace WarpTalk.TranslationRoomService.Domain.Configuration;

public class ArtifactFinalizationSettings
{
    public int MaxLocalRetries { get; set; } = 3;
    public int MaxRecoverySweeps { get; set; } = 5;
    public string StorageBaseUrl { get; set; } = "https://storage.warptalk.internal";
    public string TranscriptPathFormat { get; set; } = "/workspace/rooms/{0}/transcript.md";
    public string SummaryPathFormat { get; set; } = "/workspace/rooms/{0}/summary.md";
    public string RecordingPathFormat { get; set; } = "/workspace/rooms/{0}/full_recording.wav";
}
