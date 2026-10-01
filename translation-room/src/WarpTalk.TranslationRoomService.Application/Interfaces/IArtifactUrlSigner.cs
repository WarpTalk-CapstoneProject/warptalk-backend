namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IArtifactUrlSigner
{
    /// <summary>
    /// A short-lived, credentialed link to an artifact in object storage.
    /// </summary>
    /// <param name="downloadFileName">
    /// The name the browser should save the file under, or null to leave the response alone.
    ///
    /// NULL IS THE PLAYBACK CASE, AND IT IS NOT AN OVERSIGHT. The record page hands the very same
    /// link to a &lt;video&gt; element, and a link that answers with
    /// <c>Content-Disposition: attachment</c> is a link a browser is entitled to save instead of
    /// play. Only the caller that is actually serving a download passes a name; the player passes
    /// null and gets the object as stored.
    /// </param>
    Task<string> CreateDownloadUrlAsync(
        string storedUrl,
        TimeSpan lifetime,
        string? downloadFileName = null,
        CancellationToken ct = default);
}
