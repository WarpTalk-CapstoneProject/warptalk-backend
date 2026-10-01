using System;

namespace WarpTalk.AuthService.Domain.Entities;

/// <summary>
/// WT-888 — a one-time phrase a person must read aloud, live, before a recording of them may
/// become a voice profile.
///
/// WHY THIS EXISTS
///     The five VOICE_PROFILE_UPLOAD checkboxes are self-attested: "this is my own voice" was the
///     only thing standing between any audio file and a clone of whoever is speaking in it. A
///     random phrase that did not exist until the server issued it cannot be in somebody else's
///     old recording, so a clip that says it was made by a person speaking now, into this
///     browser, for this account.
///
/// WHY A ROW AND NOT A CACHE ENTRY
///     It is also the evidence. When somebody later says "that voice is not mine", the row says
///     which phrase was issued, when, what the recording was heard to say, and how closely it
///     matched — the same reason consent is a dated row and not a flag.
/// </summary>
public class VoiceEnrollmentChallenge
{
    public Guid Id { get; set; }

    /// <summary>The account the phrase was issued to. A challenge is never usable by anyone else.</summary>
    public Guid UserId { get; set; }

    /// <summary>The bare language code the phrase is written in ("en", "vi", "ja").</summary>
    public string Language { get; set; } = null!;

    /// <summary>The words to be read, exactly as shown.</summary>
    public string Phrase { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When a recording was checked against this phrase. Set once, atomically, whichever way the
    /// check went: a phrase is good for one attempt, so a failed one asks for a new phrase.
    /// </summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>See <see cref="Constants.VoiceEnrollmentOutcomes"/>. Null until consumed.</summary>
    public string? Outcome { get; set; }

    /// <summary>What the speech-to-text provider heard. Null until consumed.</summary>
    public string? Transcript { get; set; }

    /// <summary>0..1 similarity between the phrase and the transcript. Null until measured.</summary>
    public decimal? MatchScore { get; set; }

    /// <summary>
    /// The profile the passing recording became. No foreign key: the row is written when the
    /// recording passes, a moment before the profile it names is committed.
    /// </summary>
    public Guid? VoiceProfileId { get; set; }

    public DateTime CreatedAt { get; set; }
}
