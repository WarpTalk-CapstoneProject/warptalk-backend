using System;

namespace WarpTalk.AuthService.Domain.Entities;

/// <summary>
/// A person's decision about their own voice being cloned, kept as a record rather than a flag.
///
/// WHY THIS IS APPEND-ONLY
///     A cloned voice is biometric data, and the question a consent record has to answer is not
///     "may we clone them today" — a boolean answers that — but "what had they agreed to at the
///     moment we cloned them, and under which version of the wording". A row that is flipped in
///     place cannot answer the second, because granting and revoking overwrite each other and the
///     history is gone. So every decision inserts a row, and the current state is the newest row
///     for that user and consent type.
///
/// WHY IT IS NOT THE SAME THING AS THE PER-ROOM TOGGLE
///     translation_room_audio_routes.voice_clone_enabled says "use my cloned voice in THIS
///     meeting". This says "you may build a voice model from my speech at all". The first is a
///     preference and changes freely; the second is permission, is dated, records the wording
///     shown, and must survive the meeting it was given in.
/// </summary>
public partial class VoiceConsent
{
    public Guid Id { get; set; }

    /// <summary>External AuthService user id. No physical FK.</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The profile this decision was made about, when it was made about one. Null for the
    /// product-wide grant, which is given before any profile exists — which is the normal case,
    /// since the grant is what allows the profile to be created.
    /// </summary>
    public Guid? VoiceProfileId { get; set; }

    /// <summary>See <see cref="Constants.VoiceConsentTypes"/>.</summary>
    public string ConsentType { get; set; } = null!;

    /// <summary>GRANTED, REVOKED or EXPIRED — the `consent_status` Postgres enum.</summary>
    public string ConsentStatus { get; set; } = null!;

    /// <summary>
    /// Which wording the person actually agreed to. Consent to a text nobody can reproduce is
    /// not consent anybody can defend, so the version travels with the decision.
    /// </summary>
    public string ConsentTextVersion { get; set; } = null!;

    public DateTime? GrantedAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>Where the decision came from. Evidence, not identity — never used to look
    /// anybody up.</summary>
    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual VoiceProfile? VoiceProfile { get; set; }
}
