namespace WarpTalk.TranslationRoomService.Domain.Configuration;

public class ArtifactFinalizationSettings
{
    public int MaxLocalRetries { get; set; } = 3;
    public int MaxRecoverySweeps { get; set; } = 5;
}
