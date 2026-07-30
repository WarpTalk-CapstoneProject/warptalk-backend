namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IArtifactUrlSigner
{
    Task<string> CreateDownloadUrlAsync(
        string storedUrl,
        TimeSpan lifetime,
        CancellationToken ct = default);
}
