namespace WarpTalk.AuthService.Domain.Constants;

/// <summary>
/// What produced a <see cref="Entities.VoiceProfile"/>. Written to a varchar column, so string
/// constants rather than a C# enum — the database already owns the vocabulary, and duplicating it
/// in two type systems is how the two come to disagree.
/// </summary>
public static class VoiceProfileSources
{
    /// <summary>A recording the person deliberately made and uploaded. Never replaced automatically.</summary>
    public const string Upload = "upload";

    /// <summary>
    /// Captured and cloned while they spoke in a meeting. Replaced by a later capture that scores
    /// better by TTS_VOICE_CLONE_UPGRADE_MARGIN — which is the whole mechanism by which the clone
    /// gets closer to the person over time.
    /// </summary>
    public const string InMeeting = "in_meeting";

    /// <summary>
    /// Not a voice of this person's at all: their PICK of a public catalogue voice, kept in the
    /// same table as a pointer row.
    ///
    /// WHY THIS HAD TO BE ADDED
    ///     The other two values describe a voice somebody OWNS. A pick describes a preference,
    ///     and it was being written with this column left at its "upload" default — so a pick and
    ///     a finished upload were identical in every field the code looked at. Provider does not
    ///     separate them either: <c>SetPreferredVoiceAsync</c> writes "cartesia", and
    ///     <c>CollectFinishedClonesAsync</c> writes the same provider onto an upload once it has
    ///     cloned, because a clone lives in the Cartesia account too.
    ///
    ///     Every "is this the library voice I picked?" test therefore answered yes for the
    ///     person's own clone. The Voice Profiles rail showed them their own clone's provider id
    ///     — a raw UUID it could not name, a personal clone not being in the public catalogue —
    ///     and the same rows appeared under "Your voices" as "Untitled profile", counted toward
    ///     their voice total, and were offered as a voice to be dubbed in.
    ///
    ///     The web had been telling the two apart by a null DisplayName, which was the only field
    ///     left that differed and which stops working the moment a pick carries the catalogue
    ///     voice's name. This column is what replaces that.
    /// </summary>
    public const string Library = "library";
}
