using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarpTalk.Shared.PlatformSettings;

/// <summary>The JSON shape a setting's value must have. Serialized as the lower-case name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingValueType>))]
public enum SettingValueType
{
    Boolean,
    Integer,
    Decimal,
    String,
    Enum,
    StringList,
    FeatureFlag,
}

/// <summary>
/// Where a value may be set. A setting always has a platform value; <see cref="Plan"/> and
/// <see cref="Workspace"/> add overrides that win over it, workspace first.
/// </summary>
[Flags]
public enum SettingScopes
{
    Platform = 1,
    Plan = 2,
    Workspace = 4,
}

/// <summary>How a change reaches the service that reads it.</summary>
public static class SettingPropagation
{
    /// <summary>Read through <see cref="IPlatformSettings"/>: live within the reader's cache TTL.</summary>
    public const string Live = "live";

    /// <summary>Read once at start-up. The console says so; the value applies after a rollout.</summary>
    public const string Restart = "restart";
}

/// <summary>The console's left navigation, in order.</summary>
public static class SettingCategories
{
    public const string General = "general";
    public const string Security = "security";
    public const string Meetings = "meetings";
    public const string Billing = "billing";
    public const string Notifications = "notifications";
    public const string Limits = "limits";
    public const string Retention = "retention";
    public const string Integrations = "integrations";
    public const string FeatureFlags = "feature_flags";

    public static readonly string[] Ordered =
    [
        General, Security, Meetings, Billing, Notifications, Limits, Retention, Integrations, FeatureFlags,
    ];
}

/// <summary>The service whose code reads a setting. Shown in the console, and used by tests.</summary>
public static class SettingOwners
{
    public const string Gateway = "gateway";
    public const string Auth = "auth";
    public const string Workspace = "workspace";
    public const string TranslationRoom = "translation-room";
    public const string Meeting = "meeting";
    public const string Billing = "billing";
    public const string Notification = "notification";
    public const string Transcript = "transcript";
    public const string AiStt = "ai-stt";
    public const string AiTranslation = "ai-translation";
    public const string AiTts = "ai-tts";
    public const string AiSuggest = "ai-suggest";
    public const string AiAssistant = "ai-assistant";
    public const string Web = "web";
}

/// <summary>
/// One entry of the registry. The registry is code, not data: a key exists because some service
/// reads it, and the reader is named in <see cref="OwningService"/>. The database only stores the
/// values an operator chose; a key with no stored value is "not set" and every reader falls back
/// to its own deploy-time configuration, then to <see cref="Default"/>.
/// </summary>
public sealed record SettingDefinition
{
    public required string Key { get; init; }
    public required string Category { get; init; }
    public required SettingValueType Type { get; init; }

    /// <summary>The value in code when nothing is stored and the reader has no deploy-time value.</summary>
    public required JsonElement Default { get; init; }

    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string OwningService { get; init; }

    public SettingScopes Scopes { get; init; } = SettingScopes.Platform;
    public string? Unit { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }

    /// <summary>For <see cref="SettingValueType.Enum"/> and, per item, <see cref="SettingValueType.StringList"/>.</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>Maximum string length, or maximum list length for a list.</summary>
    public int? MaxLength { get; init; }

    /// <summary>A .NET regular expression every string (or list item) must match.</summary>
    public string? Pattern { get; init; }

    /// <summary><see cref="SettingPropagation.Live"/> or <see cref="SettingPropagation.Restart"/>.</summary>
    public string Propagation { get; init; } = SettingPropagation.Live;

    /// <summary>
    /// A change needs a written reason and a diff confirmation: it can lock people out, stop
    /// meetings or change what customers pay.
    /// </summary>
    public bool Risky { get; init; }

    /// <summary>
    /// The value is left out of exports and shown redacted in history. No secret is ever a setting
    /// (secrets stay in deploy configuration); this is for values that identify people.
    /// </summary>
    public bool Sensitive { get; init; }

    /// <summary>Editing needs <c>settings.security</c> on top of <c>settings.manage</c>.</summary>
    public bool RequiresSecurityPermission => Category == SettingCategories.Security;

    public bool RequiresRestart => Propagation == SettingPropagation.Restart;

    public bool AllowsScope(SettingScopes scope) => (Scopes & scope) == scope;
}
