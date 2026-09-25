using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarpTalk.Shared.PlatformSettings;

/// <summary>
/// The value of a <see cref="SettingValueType.FeatureFlag"/> setting.
///
/// Evaluation order, first match wins:
/// <list type="number">
/// <item><see cref="Enabled"/> false — off for everyone. This is the kill switch: it beats every
/// allow list, so one click turns a misbehaving feature off platform-wide.</item>
/// <item>The workspace is in <see cref="DenyWorkspaces"/> — off.</item>
/// <item>The workspace is in <see cref="AllowWorkspaces"/>, or its plan in <see cref="AllowPlans"/> — on.</item>
/// <item>Otherwise the workspace's bucket (0–99) is below <see cref="RolloutPercent"/>.</item>
/// </list>
/// A call with no workspace (a platform-level decision) is on only at 100%.
///
/// The bucket is the first four bytes of SHA-256("{flag key}:{workspace id}"), big-endian, mod 100.
/// The Python reader in warptalk-ai uses the same formula, and both repos test the same vectors,
/// so a workspace is either in a rollout everywhere or nowhere.
/// </summary>
public sealed record FeatureFlagValue
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("rolloutPercent")]
    public int RolloutPercent { get; init; } = 100;

    [JsonPropertyName("allowPlans")]
    public IReadOnlyList<string> AllowPlans { get; init; } = [];

    [JsonPropertyName("allowWorkspaces")]
    public IReadOnlyList<string> AllowWorkspaces { get; init; } = [];

    [JsonPropertyName("denyWorkspaces")]
    public IReadOnlyList<string> DenyWorkspaces { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static FeatureFlagValue On() => new() { Enabled = true, RolloutPercent = 100 };

    public static FeatureFlagValue Off() => new() { Enabled = false, RolloutPercent = 0 };

    public static FeatureFlagValue? TryParse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return value.Deserialize<FeatureFlagValue>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, Json);

    public bool IsEnabledFor(string flagKey, Guid? workspaceId, string? planSlug)
    {
        if (!Enabled) return false;
        if (workspaceId is not { } ws) return RolloutPercent >= 100;

        var id = ws.ToString("D");
        if (DenyWorkspaces.Contains(id, StringComparer.OrdinalIgnoreCase)) return false;
        if (AllowWorkspaces.Contains(id, StringComparer.OrdinalIgnoreCase)) return true;
        if (planSlug is not null && AllowPlans.Contains(planSlug, StringComparer.OrdinalIgnoreCase)) return true;
        if (RolloutPercent >= 100) return true;
        if (RolloutPercent <= 0) return false;
        return Bucket(flagKey, id) < RolloutPercent;
    }

    public static int Bucket(string flagKey, string subject)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{flagKey}:{subject.ToLowerInvariant()}"), hash);
        var head = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
        return (int)(head % 100);
    }
}
