using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarpTalk.WorkspaceService.Application.Entitlements;

/// <summary>
/// WT-263: entitlement key names, as published by BillingService's resolver.
///
/// Duplicated here rather than shared through a common assembly on purpose — these are WIRE values,
/// and a shared constant would let a rename in billing silently retarget this service's enforcement
/// at a key that no longer exists. The contract test pins the two lists against each other.
/// </summary>
public static class EntitlementKeys
{
    public const string MaxLanguages = "max_languages";
    public const string MaxActiveRooms = "max_active_rooms";
    public const string MaxParticipants = "max_participants";
    public const string VoiceClone = "voice_clone";
    public const string AiAssistant = "ai_assistant";
    public const string Glossary = "glossary";
}

/// <summary>One entry of the stored snapshot: the value and the layer that decided it.</summary>
public sealed record StoredEntitlement(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("source")] string Source);

/// <summary>
/// A parsed local entitlement snapshot, or the absence of one.
///
/// COLD START — the one place a fallback policy is still genuinely needed. A workspace with no
/// snapshot row has never been resolved: either it is brand new and its trial subscription's event
/// has not landed yet, or it predates this feature and the backfill has not run.
///
/// The policy is NOT ENFORCED, and it is chosen rather than defaulted to:
///
///  - It is the SAME answer the system already gives a workspace with no live subscription. WT-262
///    deliberately does not enforce plan quotas there ("no subscription" must never become "no
///    meetings"), and "never resolved" is indistinguishable from "nothing to enforce" without asking
///    billing — which is the call this ticket exists to delete.
///  - The exposure is bounded and short. Creating a workspace creates its trial subscription, which
///    publishes entitlements.changed, so a workspace reaches a snapshot within one event of
///    existing. The gap is a startup race, not a steady state.
///  - The alternative — applying platform defaults on cold start — is actively wrong: it would cap
///    a paying Enterprise workspace at the free tier's 2 languages until an unrelated billing event
///    happened to fire. Denying instead would reintroduce exactly the fail-closed coupling to
///    billing availability that WT-263 removes.
///
/// Workspace-level policy (AllowedTargetLanguages, and the settings-JSON MaxActiveRooms fallback)
/// still applies during cold start, so a cold-start workspace is not ungoverned — only its PLAN
/// quotas are not in force.
/// </summary>
public sealed class WorkspaceEntitlements
{
    private readonly IReadOnlyDictionary<string, StoredEntitlement> _entitlements;

    private WorkspaceEntitlements(
        bool isKnown,
        bool hasActiveSubscription,
        IReadOnlyDictionary<string, StoredEntitlement> entitlements)
    {
        IsKnown = isKnown;
        HasActiveSubscription = hasActiveSubscription;
        _entitlements = entitlements;
    }

    /// <summary>False when this workspace has no snapshot yet — cold start.</summary>
    public bool IsKnown { get; }

    public bool HasActiveSubscription { get; }

    /// <summary>The cold-start value: nothing known, therefore no plan quota in force.</summary>
    public static WorkspaceEntitlements Unknown { get; } = new(
        false,
        false,
        new Dictionary<string, StoredEntitlement>(StringComparer.Ordinal));

    public static WorkspaceEntitlements FromSnapshot(string? entitlementsJson, bool hasActiveSubscription)
    {
        if (string.IsNullOrWhiteSpace(entitlementsJson))
        {
            return Unknown;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, StoredEntitlement>>(entitlementsJson);
            if (parsed == null || parsed.Count == 0)
            {
                return Unknown;
            }

            return new WorkspaceEntitlements(true, hasActiveSubscription, parsed);
        }
        catch (JsonException)
        {
            // An unreadable snapshot is treated as no snapshot, not as a denial. A corrupt local
            // cache must not be able to lock a workspace out of its own product.
            return Unknown;
        }
    }

    /// <summary>
    /// The numeric limit for <paramref name="key"/>, or null when it is not in force — cold start,
    /// no live subscription, an absent key, or a value that will not parse. A null answer means
    /// "this quota does not apply", which every caller must handle without denying.
    /// </summary>
    public long? Limit(string key)
    {
        if (!IsKnown || !HasActiveSubscription)
        {
            return null;
        }

        if (!_entitlements.TryGetValue(key, out var entitlement))
        {
            return null;
        }

        return long.TryParse(entitlement.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Like <see cref="Limit"/>, but for limits the WORKSPACE may also set for itself. Those are
    /// resolved even without a live subscription: a workspace that tightened its own room cap meant
    /// it regardless of whether it is paying, and the resolver has already clamped the value to the
    /// plan ceiling.
    /// </summary>
    public long? SelfServiceLimit(string key)
    {
        if (!IsKnown || !_entitlements.TryGetValue(key, out var entitlement))
        {
            return null;
        }

        return long.TryParse(entitlement.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>The provenance of a key, for diagnostics and error copy. Null when not known.</summary>
    public string? Source(string key) =>
        _entitlements.TryGetValue(key, out var entitlement) ? entitlement.Source : null;

    public bool Flag(string key) =>
        IsKnown
        && _entitlements.TryGetValue(key, out var entitlement)
        && bool.TryParse(entitlement.Value, out var parsed)
        && parsed;
}
