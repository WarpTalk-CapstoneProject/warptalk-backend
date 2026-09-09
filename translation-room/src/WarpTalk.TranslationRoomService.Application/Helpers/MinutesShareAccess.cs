using System;
using System.Security.Cryptography;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>What a share link lets the person holding it do.</summary>
public enum ShareDecision
{
    /// <summary>Open it.</summary>
    Granted,

    /// <summary>
    /// No such live link. Returned for a token that never existed, one that was revoked and one
    /// that has expired — deliberately the same answer, so a URL cannot be probed for whether it
    /// used to work.
    /// </summary>
    NoSuchLink,

    /// <summary>
    /// The link is real and restricted, and the viewer is anonymous. Distinct from
    /// <see cref="Forbidden"/> so the page can offer a sign-in instead of a dead end — an invited
    /// person clicking their own link is not doing anything wrong, they just are not signed in.
    /// </summary>
    SignInRequired,

    /// <summary>Signed in, and not on the list.</summary>
    Forbidden
}

/// <summary>
/// Whether a share link opens, and the secret that names it.
///
/// Pure and separate from the service because this is the rule the whole feature rests on: get it
/// wrong and a company's minutes are on the open internet. It is worth being able to state it in
/// one screen and test it without a database.
/// </summary>
public static class MinutesShareAccess
{
    /// <summary>
    /// 32 bytes. Long enough that guessing is not a strategy, short enough to survive being pasted
    /// into a chat window without wrapping.
    /// </summary>
    private const int TokenBytes = 32;

    /// <summary>
    /// A fresh link secret: URL-safe Base64 over cryptographic randomness.
    ///
    /// Never derived from the room id, the minutes number or a counter. A token somebody can
    /// compute from a document they already know about is not a secret, and "BB-2026-0002" is a
    /// very short walk from "BB-2026-0001".
    /// </summary>
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <param name="link">The row the token resolved to, or null when it resolved to nothing.</param>
    /// <param name="viewerSignedIn">Whether the request carried a signed-in user at all.</param>
    /// <param name="viewerMayRead">
    /// Whether this viewer is named on the share list, or is already entitled to the minutes by
    /// the ordinary room rules. Host and participants keep their own access: a restricted link is
    /// a widening of who may read, never a narrowing.
    /// </param>
    /// <param name="now">Injected rather than read from the clock, so expiry is testable.</param>
    public static ShareDecision Decide(
        MeetingMinutesShareLink? link,
        bool viewerSignedIn,
        bool viewerMayRead,
        DateTime now)
    {
        // Revoked, expired and never-existed collapse into one answer on purpose. Distinguishing
        // them would turn the endpoint into an oracle for which documents exist.
        if (link == null || !link.IsLive(now)) return ShareDecision.NoSuchLink;

        if (string.Equals(link.AccessMode, MeetingMinutesConstants.ShareModeAnyoneWithLink, StringComparison.Ordinal))
        {
            return ShareDecision.Granted;
        }

        if (viewerMayRead) return ShareDecision.Granted;

        // An invited person who simply has not signed in yet is not a refusal; it is a sign-in.
        return viewerSignedIn ? ShareDecision.Forbidden : ShareDecision.SignInRequired;
    }

    /// <summary>
    /// Whether a document in this state may go out through a link at all.
    ///
    /// A DRAFT is not published — that is the rule the whole record lifecycle turns on: DRAFT to
    /// IN_REVIEW is a person signing their name, and until then the document is a model's output
    /// that nobody has checked. The ordinary read gate already keeps a draft with the host and the
    /// workspace's owners; a share link that served one would walk straight around that and put an
    /// unchecked machine draft in front of a client.
    ///
    /// The link itself stays valid. It simply starts opening the moment somebody signs, which is
    /// also the moment the document becomes theirs rather than the model's.
    /// </summary>
    public static bool IsServable(string? minutesStatus) =>
        !string.Equals(minutesStatus, MeetingMinutesConstants.StatusDraft, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a mode string is one this system knows. Guarded at the edge rather than trusted,
    /// because an unrecognised mode written into the column would be read back by
    /// <see cref="Decide"/> as "not public", and a link nobody can open looks like data loss.
    /// </summary>
    public static bool IsKnownMode(string? mode) =>
        string.Equals(mode, MeetingMinutesConstants.ShareModeInvitedOnly, StringComparison.Ordinal)
        || string.Equals(mode, MeetingMinutesConstants.ShareModeAnyoneWithLink, StringComparison.Ordinal);
}
