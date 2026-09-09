using System;
using System.Globalization;
using System.Text.Json;

namespace WarpTalk.Shared;

/// <summary>
/// WT-605 — the durable "this room's transcript is paused right now" flag, and the single place
/// its name, value shape and lifetime are written down.
///
/// A CROSS-REPO CONTRACT. Three processes touch this key and none of them share a database:
/// TranscriptService writes it (Pause, Resume, room end), the Gateway reads it before it
/// broadcasts a transcript segment, and warptalk-ai reads it in ai_assistant_worker and
/// suggestion_worker so a paused room's segments are skipped there too. Renaming it does not
/// break a reader, it silently turns that reader's gate off — which is why the string is
/// computed here rather than spelled out at five call sites. <see cref="ModelConfidence"/> exists
/// for the same reason applied to a different shared rule.
///
/// PRESENCE IS THE SIGNAL. The key exists ⇔ the room's transcript is paused. Resume and room end
/// DELETE it; nothing ever writes a "false". A reader should test existence (Python:
/// <c>await redis.exists(key)</c>) and must never require the payload to parse — the JSON below is
/// diagnostic, so an operator staring at Redis mid-incident can see when the pause started.
///
/// It is NOT the source of truth. <c>transcript.transcript_pause_windows</c> is, and it is what the
/// saved record and the panel divider are built from. This key is a projection for the hot paths
/// that cannot reach that table: a Python worker with no database credentials, and a Gateway
/// consumer group that deliberately never touches TranscriptService's database. A projection can
/// be stale or absent, so every reader fails OPEN — a Redis it cannot reach must not start
/// discarding a transcript nobody asked to pause.
/// </summary>
public static class TranscriptPauseKey
{
    /// <summary>
    /// How long an untouched flag survives.
    ///
    /// Twelve hours: comfortably past any meeting anybody would sit through (an all-day workshop
    /// runs eight), and short enough that a flag orphaned by a lost Resume — Redis unreachable at
    /// exactly the wrong second, a pod killed mid-call — clears itself the same day rather than
    /// outliving the meeting forever. The TTL is the backstop and not the lifecycle: Resume and
    /// room end both delete the key explicitly, and the database keeps the window either way.
    ///
    /// Deliberately unlike <c>AiPolicyTtl</c> (4h) next door, which caches an answer that cannot
    /// change during a meeting. This one can flip several times in a meeting, so it is written on
    /// every transition rather than cached for a window.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    public static string For(Guid translationRoomId) => For(translationRoomId.ToString());

    public static string For(string translationRoomId) =>
        $"translationRoom:{translationRoomId}:transcript_paused";

    /// <summary>
    /// The diagnostic body. snake_case because the readers that will ever parse it are Python,
    /// matching <c>translationRoom:{id}:ai_policy</c>'s <c>allow_external_llm</c> next door.
    /// </summary>
    public static string Payload(DateTime pausedAtUtc) =>
        JsonSerializer.Serialize(new
        {
            paused = true,
            paused_at = pausedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        });
}
