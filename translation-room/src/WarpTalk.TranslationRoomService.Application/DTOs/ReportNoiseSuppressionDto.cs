namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// What the CLIENT's own denoiser ended up doing, reported by the browser that ran it.
///
/// WHY THE SERVER HAS TO BE TOLD
///     Enhanced noise suppression is Krisp, and Krisp runs entirely in the participant's browser.
///     Whether it is actually running is decided there and nowhere else — and until this existed
///     the answer was never recorded anywhere a person could read afterwards. The only trace of a
///     failure was a toast and a console.error in one participant's tab.
///
///     That matters more than it sounds, because the failure is SILENT BY CONSTRUCTION. Attaching
///     the processor succeeds; enabling it asks the LiveKit project whether it is entitled, and
///     livekit-client calls that path un-awaited and un-caught, so a denial rejects nothing. The
///     web code has to check isEnabled() by hand to notice at all (see use-track-processors.ts).
///     A feature whose success is invisible to the team cannot be operated: "is noise suppression
///     working in production" had no answer that did not involve joining a meeting and looking.
///
/// NOT A SETTING. Nothing on the server changes because of this — it is an observation about what
/// already happened, which is why it is a report and not a Set. The participant's own preference
/// lives in their browser; the STT-side denoising mode next door is a different denoiser at a
/// different stage, and this deliberately does not touch it.
/// </summary>
/// <param name="Enabled">
/// Whether the ENHANCED filter is genuinely running — attached AND enabled, not merely attached.
/// False means the microphone fell back to the browser's own suppression, which is a downgrade
/// and not an outage.
/// </param>
/// <param name="Processor">Which denoiser is carrying the load: "krisp" or "browser".</param>
/// <param name="Reason">
/// When <paramref name="Enabled"/> is false, the client's own description of why. Free text from a
/// browser, so it is bounded before it reaches a log line.
/// </param>
public record ReportNoiseSuppressionDto(bool Enabled, string Processor, string? Reason);
