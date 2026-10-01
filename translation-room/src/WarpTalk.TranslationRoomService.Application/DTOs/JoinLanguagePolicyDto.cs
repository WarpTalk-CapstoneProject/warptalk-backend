using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// What the pre-join screen may offer for one room code — the two limits, kept separate.
///
/// WT-468 answered the first: a joiner sees what the workspace that OWNS the room permits, not
/// what their own selected workspace permits. WT-490 is the second, and the reason this became a
/// record rather than staying a bare list of languages: a room declares the set of languages that
/// will be spoken in it, and the screen was offering every language the workspace permitted
/// regardless. A workspace allowing four languages and a room declaring two offered four, so a
/// joiner could pick a language nobody in the room would ever speak.
///
/// They stay two fields rather than one pre-intersected list because they fail differently and the
/// caller has to be able to tell them apart. An empty list means "no restriction from this source"
/// in both cases — an unknown or half-typed code, or a workspace with no policy — and intersecting
/// here would turn either empty into "offer nothing", which is the one answer that leaves a user
/// unable to join at all.
/// </summary>
public record JoinLanguagePolicyDto(
    /// <summary>Bare codes the room's workspace permits. Empty means unrestricted.</summary>
    IReadOnlyList<string> AllowedTargetLanguages,
    /// <summary>
    /// Bare codes this room itself declares — its source language plus its targets. Empty when the
    /// code resolves to no room, which every consumer reads as "no restriction" for the same reason
    /// the endpoint answers 200 for a half-typed code.
    /// </summary>
    IReadOnlyList<string> RoomLanguages,
    /// <summary>
    /// WT-866: the code resolves to a room that is over (ENDED, CANCELLED or EXPIRED), so the
    /// pre-join screen should say so instead of opening camera and microphone for a join the
    /// server will refuse. Not a new oracle: the join itself already answers "already ended"
    /// for such a code and "not found" for an unknown one.
    /// </summary>
    bool RoomEnded = false
);
