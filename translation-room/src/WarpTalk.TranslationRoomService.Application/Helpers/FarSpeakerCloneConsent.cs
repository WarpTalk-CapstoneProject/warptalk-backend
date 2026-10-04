using System;
using System.Security.Cryptography;
using System.Text;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-933. The naming half of "the host records which Meet-side people agreed to voice cloning".
///
/// Everyone on the Meet side of an EXTERNAL_BRIDGE room is published under ONE stand-in
/// participant, so the only thing that tells two of them apart is the Meet display name the
/// captions carry. Consent is therefore recorded per NAME, and — because a name is personal data
/// that has no business sitting in Redis or in a log line — what is recorded is a digest of it.
///
/// THE OTHER HALF IS IN ANOTHER REPOSITORY
///     warptalk-ai's <c>tts_worker/far_speaker_clone.py</c> computes the same field from the name
///     on a transcript segment and looks it up in the hash this service writes. Nothing but the
///     two functions below agreeing byte for byte connects the two, so they are pure, static and
///     pinned by test vectors. Changing either one silently withdraws every consent in flight.
/// </summary>
public static class FarSpeakerCloneConsent
{
    /// <summary>The longest display name a consent can be recorded for. Longer is a 400.</summary>
    public const int MaxDisplayNameLength = 100;

    /// <summary>The most names one status question may carry. More is a 400.</summary>
    public const int MaxStatusNames = 50;

    /// <summary>How many hex chars of the field a log line may show — enough to correlate, not to reverse.</summary>
    public const int LogPrefixLength = 12;

    /// <summary>
    /// Long enough to outlive any meeting, short enough that an abandoned room's consents do not
    /// sit in Redis forever. Refreshed at each write.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public const string ErrorOnlyHostCanRecord = "Only the meeting host can record who agreed to voice cloning.";
    public const string ErrorDisplayNameRequired = "A display name is required.";
    public const string ErrorDisplayNameTooLong = "A display name can be at most 100 characters.";
    public const string ErrorDisplayNamesRequired = "A list of display names is required.";
    public const string ErrorTooManyDisplayNames = "At most 50 display names can be checked at once.";
    public const string ErrorCouldNotWrite = "Could not record the voice cloning consent right now.";
    public const string ErrorCouldNotRead = "Could not read the voice cloning consents right now.";

    /// <summary>
    /// The hash the AI side reads. <c>{roomId}</c> is the lower-case hyphenated GUID, which is
    /// what <see cref="Guid.ToString()"/> gives — spelled "D" here so nobody has to know that.
    /// </summary>
    public static string KeyFor(Guid roomId) =>
        $"translationRoom:{roomId.ToString("D")}:far_speaker_clone_consents";

    /// <summary>
    /// The contract's name normalisation: Unicode NFKC, <see cref="string.ToLowerInvariant"/>,
    /// then whitespace runs collapsed to one space and the ends trimmed. The Python twin is
    /// <c>" ".join(unicodedata.normalize("NFKC", name).lower().split())</c>.
    ///
    /// Empty (or null, or whitespace only) folds to empty, which is not a name: no consent can be
    /// recorded for it.
    /// </summary>
    public static string Fold(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var lowered = name.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

        // Split with no separators splits on char.IsWhiteSpace — the same notion of whitespace
        // Python's str.split() uses — and RemoveEmptyEntries is what collapses the runs.
        var words = lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words);
    }

    /// <summary>
    /// The Redis hash field for a Meet display name: lower-case hex SHA-256, all 64 chars, of the
    /// UTF-8 bytes of <c>"{stand-in user id}:{fold(name)}"</c>. Null for a name that folds to
    /// nothing — there is no field for it, so there is nothing to write or to look up.
    /// </summary>
    public static string? ConsentField(string? name)
    {
        var folded = Fold(name);
        if (folded.Length == 0)
            return null;

        var standIn = TranslationRoomConstants.ExternalBridgeParticipantUserId.ToString("D");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{standIn}:{folded}"));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>What a log line shows instead of the name: the first 12 hex chars of the field.</summary>
    public static string LogPrefix(string field) =>
        field.Length <= LogPrefixLength ? field : field[..LogPrefixLength];
}
