namespace WarpTalk.TranslationRoomService.Domain.Configuration;

public class TelemetrySettings
{
    public double SttDegradedMs { get; set; } = 3000.0;
    public double SttRecoveryMs { get; set; } = 1500.0;
    
    public double TranslationDegradedMs { get; set; } = 2500.0;
    public double TranslationRecoveryMs { get; set; } = 1200.0;
    
    public double TtsDegradedMs { get; set; } = 6000.0;
    public double TtsRecoveryMs { get; set; } = 3000.0;
    
    public int WarmupCount { get; set; } = 3;
}
