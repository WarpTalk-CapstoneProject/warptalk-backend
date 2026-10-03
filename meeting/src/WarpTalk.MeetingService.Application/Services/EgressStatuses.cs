using System.Text.Json;

namespace WarpTalk.MeetingService.Application.Services;

/// <summary>
/// LiveKit's EgressStatus as it appears in an EgressInfo (webhook body or ListEgress item). Twirp
/// JSON serialises the proto enum as its name, some clients send the ordinal; both are accepted.
/// </summary>
public static class EgressStatuses
{
    private static readonly string[] Names =
    [
        "EGRESS_STARTING",      // 0
        "EGRESS_ACTIVE",        // 1
        "EGRESS_ENDING",        // 2
        "EGRESS_COMPLETE",      // 3
        "EGRESS_FAILED",        // 4
        "EGRESS_ABORTED",       // 5
        "EGRESS_LIMIT_REACHED", // 6
    ];

    /// <summary>
    /// The status ordinal. A missing status is 0 (STARTING) — proto3 omits the zero value. Anything
    /// this code does not know — an unknown name, an ordinal outside 0..6, another value kind — is
    /// -1, which is neither capturing nor terminal, the same for both spellings.
    /// </summary>
    public static int Ordinal(JsonElement egressInfo)
    {
        if (!egressInfo.TryGetProperty("status", out var status))
            return 0;

        if (status.ValueKind == JsonValueKind.String)
        {
            var name = status.GetString();
            return Array.FindIndex(Names, known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));
        }

        return status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var ordinal)
            && ordinal >= 0
            && ordinal < Names.Length
                ? ordinal
                : -1;
    }

    /// <summary>COMPLETE, FAILED, ABORTED, LIMIT_REACHED: the egress is over.</summary>
    public static bool IsTerminal(JsonElement egressInfo) => Ordinal(egressInfo) >= 3;

    /// <summary>STARTING or ACTIVE: still capturing. ENDING means somebody already stopped it.</summary>
    public static bool IsCapturing(JsonElement egressInfo) => Ordinal(egressInfo) is 0 or 1;

    /// <summary>
    /// ENDING or terminal: somebody already stopped it, or it is over. An unknown status is neither
    /// this nor capturing — a caller that must not report a running recording stopped reads it as "no".
    /// </summary>
    public static bool IsEndingOrOver(JsonElement egressInfo) => Ordinal(egressInfo) >= 2;
}
