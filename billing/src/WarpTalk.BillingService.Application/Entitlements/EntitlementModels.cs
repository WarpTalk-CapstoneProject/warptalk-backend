using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Entitlements;

/// <summary>
/// WT-263: one resolved entitlement and the layer that decided it.
///
/// <paramref name="Source"/> is not decoration. It is what makes the map auditable: "your plan
/// allows 3" and "your workspace owner capped you at 2" are different answers to the same question,
/// and without provenance a limit is unexplainable to the person hitting it.
/// </summary>
public sealed record ResolvedEntitlement(string Key, string Value, string Source)
{
    public static ResolvedEntitlement Number(string key, long value, string source) =>
        new(key, value.ToString(CultureInfo.InvariantCulture), source);

    public static ResolvedEntitlement Flag(string key, bool value, string source) =>
        new(key, value ? "true" : "false", source);

    /// <summary>InvariantCulture on purpose — a machine-readable wire value must not follow the
    /// ambient locale. WarpTalk has already shipped a billing bug where it did.</summary>
    public long AsNumber() => long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : 0;

    public bool AsFlag() => bool.TryParse(Value, out var parsed) && parsed;
}

/// <summary>
/// Everything the resolver is allowed to look at, gathered by its caller.
///
/// The resolver takes its inputs rather than fetching them so the resolution ORDER can be tested as
/// a pure function, with no database and no clock. Whether a subscription counts as live is decided
/// before this record is built (<see cref="HasActiveSubscription"/>); the resolver never re-derives
/// it, because "is this plan in force" and "what does this plan allow" are separate questions and
/// mixing them is how the old code ended up unable to answer either.
/// </summary>
/// <param name="Plan">The catalog row, or null when the workspace has no plan at all.</param>
/// <param name="HasActiveSubscription">
/// False when there is no live paid plan. The plan's numbers are then NOT in force: the resolver
/// falls back to the platform defaults rather than enforcing quotas nobody is paying for. This
/// preserves the WT-262 rule that "no subscription" must never become "no meetings".
/// </param>
/// <param name="ContractOverrides">
/// Negotiated per-contract values. These may loosen as well as tighten — a contract IS the
/// agreement with the platform, so it outranks the catalog row in both directions.
/// </param>
/// <param name="WorkspaceOverrides">
/// Self-service values chosen by the workspace owner. These may only TIGHTEN; see
/// <see cref="EntitlementResolver"/>.
/// </param>
public sealed record EntitlementResolutionInputs(
    Plan? Plan,
    bool HasActiveSubscription,
    IReadOnlyDictionary<string, string> ContractOverrides,
    IReadOnlyDictionary<string, string> WorkspaceOverrides)
{
    public static EntitlementResolutionInputs None { get; } = new(
        null,
        false,
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>The resolved map for one workspace: every key in
/// <see cref="EntitlementConstants.Keys.All"/>, each with a value and a provenance.</summary>
public sealed record WorkspaceEntitlementMap(
    Guid WorkspaceId,
    string? PlanSlug,
    bool HasActiveSubscription,
    DateTime ResolvedAt,
    IReadOnlyList<ResolvedEntitlement> Entitlements)
{
    public ResolvedEntitlement this[string key] =>
        Entitlements.FirstOrDefault(entitlement => entitlement.Key == key)
        ?? throw new KeyNotFoundException(string.Format(EntitlementConstants.Errors.UnknownEntitlementKey, key));

    public long Number(string key) => this[key].AsNumber();

    public bool Flag(string key) => this[key].AsFlag();

    public string Source(string key) => this[key].Source;
}
