using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// WT-888 — turn one short recording into text, so a voice-profile recording can be checked
/// against the phrase the person was asked to read.
///
/// A failure is <see cref="ErrorCodes.ServiceUnavailable"/> — the recording was not judged, which
/// is different from it being judged and found wanting. Implementations must never put the
/// challenge phrase into the request (as a decoding prompt, say): biasing the model towards the
/// answer is exactly how a recording that does not say it would be heard to say it.
/// </summary>
public interface ISpeechTranscriber
{
    Task<Result<string>> TranscribeAsync(
        byte[] audio,
        string fileName,
        string contentType,
        string language,
        CancellationToken ct = default);
}
