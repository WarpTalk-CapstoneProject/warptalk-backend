using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// One "this speaker now has a permanent voice" announcement from the AI side.
/// </summary>
/// <param name="MessageId">The stream entry, so it can be acknowledged once it is applied.</param>
/// <param name="UserId">Whose voice it is. The AI side knows people by auth user id.</param>
/// <param name="Language">The language the voice was cloned in.</param>
/// <param name="VoiceId">The provider voice, ALREADY RENAMED out of the orphan sweep's sights.</param>
/// <param name="Score">
/// How good the clip behind it was (0..1), or null when nobody measured it.
///
/// Null is not zero and must never be coerced to it: zero grades as the worst possible sample
/// and invites replacement by literally any clip that clears the floors.
/// </param>
public sealed record VoiceCarryOverMessage(
    string MessageId,
    Guid UserId,
    string Language,
    string VoiceId,
    decimal? Score);

/// <summary>
/// The AI side announcing clones worth keeping, and this side asking for voices to be destroyed.
///
/// WHY THIS ONE IS CONSUMED WHEN <see cref="IVoiceCloneRequestQueue"/> IS PULLED LAZILY
///     That interface says, correctly, that a background consumer would be "a new lifecycle, a
///     new failure mode, and a new thing to watch, to deliver an answer nobody sees until they
///     open the page". Every word of that holds for an uploaded recording: the answer is wanted
///     on the voice-profiles page, so it is collected when that page is opened.
///
///     This answer has no page. It is wanted by the ROUTE BUILD at the start of the person's
///     next meeting, and nothing guarantees they visit any page between the two. Pulled lazily
///     it would be a feature that works only for people who happen to browse their voice
///     settings — which is the shape of a fix that was written and wired to nothing.
///
/// WHY DELETION GOES THE OTHER WAY
///     This service holds the row and can forget a voice; only the AI side holds the Cartesia
///     key that can destroy one. So a deletion decided here has to be asked for there.
/// </summary>
public interface IVoiceCarryOverQueue
{
    /// <summary>
    /// Announcements waiting to be applied. Empty is the ordinary answer and never an error.
    /// </summary>
    Task<IReadOnlyList<VoiceCarryOverMessage>> ReadAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Mark one announcement applied. Called only AFTER the row is committed, so a crash between
    /// the two redelivers rather than loses — the upsert is idempotent for exactly this reason.
    /// </summary>
    Task AcknowledgeAsync(string messageId, CancellationToken ct = default);

    /// <summary>
    /// Ask the AI side to destroy a provider voice.
    ///
    /// Best-effort in the sense that it cannot confirm the deletion happened — but it must not be
    /// skipped, because for withdrawn consent this is the difference between a promise kept and
    /// a promise broken. <paramref name="reason"/> is carried so the AI side's log says which of
    /// the two callers asked.
    /// </summary>
    Task RequestDeletionAsync(string voiceId, string reason, CancellationToken ct = default);
}
