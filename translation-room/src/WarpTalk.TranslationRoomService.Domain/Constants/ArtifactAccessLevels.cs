using System;
using System.Linq;

namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// The vocabulary of <c>TranslationRoomSettings.ArtifactAccess</c> — the room policy that decides
/// who, besides the host, may reach a room's artifacts.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the field had no shared constant and no validation, and that shape produced
/// a policy that had never once worked. The writers persist SCREAMING_SNAKE (<c>HOST_ONLY</c> from
/// <c>TranslationRoomMapper.ResolveSettings</c>, <c>ALL_PARTICIPANTS</c> from a host who changes
/// it), while the guard that read the field compared against <c>nameof</c> of a PascalCase enum —
/// <c>"Participants"</c> and <c>"Workspace"</c>, strings the system has never written. The two
/// spellings could never meet, so a host who opened a room to its participants still saw every one
/// of them refused. It failed closed, which is why it read as "policy enforced" for so long.
/// </para>
/// <para>
/// The fix is the shape, not just the comparison: one place names the values, one predicate decides
/// validity, and <c>UpdateTranslationRoomSettingsAsync</c> rejects anything else on the way in, so
/// an unrecognised level can no longer reach the database and quietly deny everybody.
/// </para>
/// <para>
/// <c>WORKSPACE</c> is deliberately absent. The retired enum listed a <c>Workspace</c> member, but
/// nothing ever wrote it and nothing here can enforce it: "any member of the workspace" is an
/// answer only WorkspaceService holds, behind a gRPC call this guard does not make. Accepting the
/// value would mean storing a policy the system silently downgrades to <c>ALL_PARTICIPANTS</c>.
/// Adding the level should be a product decision that ships with the membership lookup.
/// </para>
/// </remarks>
public static class ArtifactAccessLevels
{
    /// <summary>Only the room's host may reach artifacts. The default for every new room.</summary>
    public const string HostOnly = "HOST_ONLY";

    /// <summary>The host and anyone who was a participant of the room.</summary>
    public const string AllParticipants = "ALL_PARTICIPANTS";

    /// <summary>Every level the system can both persist and enforce.</summary>
    public static readonly string[] All = { HostOnly, AllParticipants };

    /// <summary>
    /// Exact, case-sensitive: these are stored tokens, not user-facing text, and every writer in
    /// the codebase emits them upper-case. Accepting "host_only" here would put a second spelling
    /// into the database and re-open exactly the mismatch this type exists to close.
    /// </summary>
    public static bool IsValid(string? value)
        => value is not null && All.Contains(value, StringComparer.Ordinal);
}
