namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// Choose how much denoising the provider applies to YOUR microphone in this meeting.
///
/// A mode string rather than a bool, unlike SetFlashModeDto beside it, because there are three
/// genuinely different answers and the middle one is not "half on":
///
///   off         — no provider-side pass. The right answer for a headset: the measured reason the
///                 deployment default is "off" is that a second pass on top of the browser's own
///                 distorted clean close-mic speech.
///   near_field  — a close-talking microphone. A headset or a handset held to the face.
///   far_field   — a microphone across the desk or the room. A laptop picking a room up from two
///                 metres away needs exactly this pass, and without it the transcript degrades
///                 into whatever the microphone is hearing.
/// </summary>
/// <param name="Mode">One of "off", "near_field", "far_field".</param>
public record SetNoiseReductionDto(string Mode);
