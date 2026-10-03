namespace WarpTalk.MeetingService.Application.Services;

/// <summary>
/// LiveKit identities that are ours rather than a person's. One list, shared by the webhook (whose
/// participant_joined must not summon the ingress bot for the bot's own join) and the bridge
/// recording end check (where none of these keeps a meeting alive). Same list as
/// livekit_ingress_worker's _AI_BOT_IDENTITY_PREFIXES.
/// </summary>
public static class LiveKitParticipantIdentities
{
    /// <summary>The ingress bot ("AIBot_{room}") and the TTS interpreters ("ai-interpreter-*").</summary>
    public static readonly IReadOnlyList<string> BotIdentityPrefixes = ["AIBot_", "ai-interpreter-"];

    /// <summary>LiveKit's own egress recorder joins as "EG_…".</summary>
    public const string EgressIdentityPrefix = "EG_";

    public static bool IsBot(string? identity) =>
        !string.IsNullOrEmpty(identity)
        && BotIdentityPrefixes.Any(prefix => identity.StartsWith(prefix, StringComparison.Ordinal));
}
