namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// Turn flash mode on or off for a room.
///
/// A bare bool rather than a mode string: there are exactly two states, and the AI side's
/// tolerance for several spellings of "on" exists for humans with redis-cli, not for this
/// endpoint, which should have one way to say each thing.
/// </summary>
/// <param name="Enabled">True to stream audio while the speaker is still talking.</param>
public record SetFlashModeDto(bool Enabled);
