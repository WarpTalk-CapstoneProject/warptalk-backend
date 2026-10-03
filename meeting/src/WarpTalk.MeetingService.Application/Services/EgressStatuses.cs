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
    /// The status ordinal. A missing status is 0 (STARTING) — proto3 omits the zero value. A name
    /// or value kind this code does not know is -1, which is neither capturing nor terminal.
    /// </summary>
    public static int Ordinal(JsonElement egressInfo)
    {
        if (!egressInfo.TryGetProperty("status", out var status))
            return 0;

        if (status.ValueKind == JsonValueKind.String)
        {
            var index = Array.FindIndex(Names, name => string.Equals(name, status.GetString(), StringComparison.OrdinalIgnoreCase));
            return index;
        }

        return status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var ordinal) ? ordinal : -1;
    }

    /// <summary>COMPLETE, FAILED, ABORTED, LIMIT_REACHED (or any later ordinal): the egress is over.</summary>
    public static bool IsTerminal(JsonElement egressInfo) => Ordinal(egressInfo) >= 3;

    /// <summary>STARTING or ACTIVE: still capturing. ENDING means somebody already stopped it.</summary>
    public static bool IsCapturing(JsonElement egressInfo) => Ordinal(egressInfo) is 0 or 1;
}
