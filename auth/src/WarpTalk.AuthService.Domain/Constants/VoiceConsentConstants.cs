namespace WarpTalk.AuthService.Domain.Constants;

/// <summary>
/// What a voice consent row can say. Values are written to a Postgres enum column
/// (`consent_status`) and to a varchar (`consent_type`), so they are string constants rather
/// than C# enums — the database already owns the vocabulary and duplicating it in two type
/// systems is how the two come to disagree.
/// </summary>
public static class VoiceConsentStatuses
{
    public const string Granted = "GRANTED";
    public const string Revoked = "REVOKED";

    /// <summary>
    /// Set by nothing today. It exists in the database enum, and a consent that ages out is a
    /// real thing to want, but no policy in this product expires one yet — so it is read (an
    /// EXPIRED row is not active consent) and never written. Writing it needs a decision about
    /// how long consent lasts, which is a product question, not a code one.
    /// </summary>
    public const string Expired = "EXPIRED";
}

public static class VoiceConsentTypes
{
    /// <summary>
    /// Permission to build a voice model from this person's speech. The only type in use.
    /// It is deliberately not per-workspace: the biometric is the person's, not the tenant's,
    /// and someone who withdraws it withdraws it everywhere.
    /// </summary>
    public const string VoiceClone = "VOICE_CLONE";
}

public static class VoiceConsentTextVersions
{
    /// <summary>
    /// The wording currently shown when consent is asked for. Bump this whenever that text
    /// changes in a way that alters what is being agreed to — an old row keeps its own version,
    /// which is the point of storing it.
    /// </summary>
    public const string Current = "2026-08-13.v1";
}
