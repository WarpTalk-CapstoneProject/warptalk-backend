using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Entitlements;

public interface IEntitlementResolver
{
    /// <summary>Loads every layer for the workspace and resolves the full map.</summary>
    Task<WorkspaceEntitlementMap> ResolveAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Whether a workspace owner may store <paramref name="requestedValue"/> for
    /// <paramref name="key"/>. Returns null when the setting is a legal tightening, or the reason it
    /// is not. Used by the write path so a loosening attempt is REJECTED at the boundary rather than
    /// silently discarded at resolution time.
    /// </summary>
    Task<string?> ValidateWorkspaceOverrideAsync(
        Guid workspaceId,
        string key,
        string requestedValue,
        CancellationToken ct = default);
}

/// <summary>
/// WT-263: the ONLY code in WarpTalk permitted to compute an entitlement.
///
/// Resolution order, lowest precedence first:
///   1. platform default   — what a workspace gets when nobody has an opinion
///   2. plan               — the catalog row the workspace is on, IF the subscription is live
///   3. contract override  — negotiated per-subscription terms; may loosen or tighten
///   4. workspace override — the owner's own setting; may ONLY tighten
///
/// THE INVARIANT lives here, in step 4, and nowhere else. A workspace may restrict itself below
/// what its plan allows, and may never raise a limit beyond it. Enforcing that in the UI, or in the
/// service that owns the setting, would mean the rule holds only for callers that go through that
/// screen — and the whole reason this layer exists is that "each caller enforces it for itself"
/// produced six different answers and no enforcement at all. A loosening value here is simply not
/// applied, and the write path rejects it outright via <see cref="ValidateWorkspaceOverrideAsync"/>.
///
/// "Tighter" means smaller for a numeric limit and false for a boolean capability
/// (see <see cref="EntitlementConstants.Keys.NumericLimits"/>).
///
/// Note what is NOT here: no fail-open/fail-closed branch, no timeouts, no remote calls. Resolution
/// happens on change, not on request. Enforcement reads a replicated snapshot, so a billing outage
/// cannot reach a permission decision at all.
/// </summary>
public sealed class EntitlementResolver : IEntitlementResolver
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public EntitlementResolver(IUnitOfWork unitOfWork, TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkspaceEntitlementMap> ResolveAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var inputs = await GatherAsync(workspaceId, nowUtc, ct);
        return Resolve(workspaceId, inputs, nowUtc);
    }

    public async Task<string?> ValidateWorkspaceOverrideAsync(
        Guid workspaceId,
        string key,
        string requestedValue,
        CancellationToken ct = default)
    {
        if (!EntitlementConstants.Keys.All.Contains(key, StringComparer.Ordinal))
        {
            return string.Format(EntitlementConstants.Errors.UnknownEntitlementKey, key);
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var inputs = await GatherAsync(workspaceId, nowUtc, ct);

        // The ceiling is everything ABOVE the workspace layer — platform, plan, contract. Comparing
        // against the already-resolved map would compare the request against the workspace's own
        // previous setting, so a workspace that had tightened to 1 could never relax back to the 3
        // its plan actually sells.
        var ceiling = ResolveCeiling(inputs);
        if (!ceiling.TryGetValue(key, out var ceilingValue))
        {
            return string.Format(EntitlementConstants.Errors.UnknownEntitlementKey, key);
        }

        return IsTighteningOrEqual(key, requestedValue, ceilingValue.Value)
            ? null
            : string.Format(EntitlementConstants.Errors.WorkspaceOverrideLoosens, key, ceilingValue.Value);
    }

    private async Task<EntitlementResolutionInputs> GatherAsync(Guid workspaceId, DateTime nowUtc, CancellationToken ct)
    {
        var subscription = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            candidate => candidate.WorkspaceId == workspaceId && candidate.DeletedAt == null,
            ct);

        Plan? plan = null;
        if (subscription != null)
        {
            plan = await _unitOfWork.Plans.GetByIdAsync(subscription.PlanId, ct);
        }

        // WT-430: one definition, on the entity. This used to be spelled out here and again in
        // GrpcBillingMapper.ToFeatureAccessResponse, with a comment asking the reader to keep the
        // two in step by hand — which is not a mechanism.
        var hasActiveSubscription = subscription?.GrantsPlanEntitlements(nowUtc) == true;

        var overrides = await _unitOfWork.WorkspaceEntitlementOverrides.GetForWorkspaceAsync(workspaceId, ct);

        return new EntitlementResolutionInputs(
            plan,
            hasActiveSubscription,
            ReadContractOverrides(subscription),
            overrides.ToDictionary(o => o.EntitlementKey, o => o.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// Contract overrides for entitlement keys live in <c>subscription.subscriptions.entitlement_overrides</c>
    /// (jsonb, migration 050). The existing typed <c>*_override</c> columns beside it cover credits,
    /// overage and invoice terms — commercial terms, not capabilities — so none of them maps to an
    /// entitlement key. One jsonb column rather than a column per key: contract terms are negotiated
    /// per deal and the set of entitlement keys grows, so a column per key would mean a migration
    /// every time a capability is added.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadContractOverrides(Subscription? subscription)
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(subscription?.EntitlementOverrides))
        {
            return empty;
        }

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                subscription!.EntitlementOverrides!);
            if (parsed == null)
            {
                return empty;
            }

            foreach (var (key, element) in parsed)
            {
                var value = element.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.True => "true",
                    System.Text.Json.JsonValueKind.False => "false",
                    System.Text.Json.JsonValueKind.Number => element.GetRawText(),
                    System.Text.Json.JsonValueKind.String => element.GetString() ?? string.Empty,
                    _ => null
                };

                if (value != null)
                {
                    empty[key] = value;
                }
            }

            return empty;
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed contract blob must not widen anything. Falling back to the plan is the
            // conservative direction here: the plan is what the workspace demonstrably bought.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The pure resolution. Exposed as a static so the ORDER can be tested without a database — the
    /// property that has to hold is about precedence, not about persistence.
    /// </summary>
    public static WorkspaceEntitlementMap Resolve(
        Guid workspaceId,
        EntitlementResolutionInputs inputs,
        DateTime resolvedAtUtc)
    {
        var ceiling = ResolveCeiling(inputs);
        var resolved = new List<ResolvedEntitlement>(EntitlementConstants.Keys.All.Length);

        foreach (var key in EntitlementConstants.Keys.All)
        {
            var current = ceiling[key];

            if (inputs.WorkspaceOverrides.TryGetValue(key, out var requested)
                && IsTighteningOrEqual(key, requested, current.Value))
            {
                // Only a tightening is applied. A loosening request is DROPPED rather than clamped
                // to the ceiling, because clamping would report source=workspace_override for a
                // value the workspace did not choose, and provenance has to stay truthful.
                current = new ResolvedEntitlement(key, Normalize(key, requested), EntitlementConstants.Sources.WorkspaceOverride);
            }

            resolved.Add(current);
        }

        return new WorkspaceEntitlementMap(
            workspaceId,
            inputs.Plan?.Slug,
            inputs.HasActiveSubscription,
            resolvedAtUtc,
            resolved);
    }

    /// <summary>Layers 1–3: everything the workspace itself cannot change.</summary>
    private static Dictionary<string, ResolvedEntitlement> ResolveCeiling(EntitlementResolutionInputs inputs)
    {
        var planSource = EntitlementConstants.Sources.Plan(inputs.Plan?.Slug);

        // Layer 1 then layer 2. The plan only speaks when its subscription is in force; otherwise
        // the platform default stands, which is what keeps "no subscription" from meaning "no
        // meetings" (the WT-262 carve-out, now expressed as an ordinary layer rather than a branch).
        var usePlan = inputs.HasActiveSubscription && inputs.Plan != null;

        var ceiling = new Dictionary<string, ResolvedEntitlement>(StringComparer.Ordinal)
        {
            [EntitlementConstants.Keys.MaxLanguages] = usePlan
                ? ResolvedEntitlement.Number(EntitlementConstants.Keys.MaxLanguages, inputs.Plan!.MaxLanguages, planSource)
                : ResolvedEntitlement.Number(EntitlementConstants.Keys.MaxLanguages, EntitlementConstants.PlatformDefaults.MaxLanguages, EntitlementConstants.Sources.PlatformDefault),

            [EntitlementConstants.Keys.MaxActiveRooms] = usePlan
                ? ResolvedEntitlement.Number(EntitlementConstants.Keys.MaxActiveRooms, inputs.Plan!.MaxActiveRooms, planSource)
                : ResolvedEntitlement.Number(EntitlementConstants.Keys.MaxActiveRooms, EntitlementConstants.PlatformDefaults.MaxActiveRooms, EntitlementConstants.Sources.PlatformDefault),

            [EntitlementConstants.Keys.MaxParticipants] = usePlan
                ? ResolvedEntitlement.Number(EntitlementConstants.Keys.MaxParticipants, inputs.Plan!.MaxParticipants, planSource)
                : ResolvedEntitlement.Number(EntitlementConstants.Keys.MaxParticipants, EntitlementConstants.PlatformDefaults.MaxParticipants, EntitlementConstants.Sources.PlatformDefault),

            [EntitlementConstants.Keys.VoiceClone] = usePlan
                ? ResolvedEntitlement.Flag(EntitlementConstants.Keys.VoiceClone, inputs.Plan!.VoiceCloneEnabled, planSource)
                : ResolvedEntitlement.Flag(EntitlementConstants.Keys.VoiceClone, EntitlementConstants.PlatformDefaults.VoiceClone, EntitlementConstants.Sources.PlatformDefault),

            [EntitlementConstants.Keys.AiAssistant] = usePlan
                ? ResolvedEntitlement.Flag(EntitlementConstants.Keys.AiAssistant, inputs.Plan!.AiAssistantEnabled, planSource)
                : ResolvedEntitlement.Flag(EntitlementConstants.Keys.AiAssistant, EntitlementConstants.PlatformDefaults.AiAssistant, EntitlementConstants.Sources.PlatformDefault),

            [EntitlementConstants.Keys.Glossary] = usePlan
                ? ResolvedEntitlement.Flag(EntitlementConstants.Keys.Glossary, inputs.Plan!.GlossaryEnabled, planSource)
                : ResolvedEntitlement.Flag(EntitlementConstants.Keys.Glossary, EntitlementConstants.PlatformDefaults.Glossary, EntitlementConstants.Sources.PlatformDefault)
        };

        // Layer 3. A contract may go either way: it is the negotiated agreement, so it outranks the
        // catalog row in both directions. It is applied even without an active subscription, because
        // a contract term that only applies while billing is healthy is not a contract term.
        foreach (var key in EntitlementConstants.Keys.All)
        {
            if (inputs.ContractOverrides.TryGetValue(key, out var contractValue)
                && TryNormalize(key, contractValue, out var normalized))
            {
                ceiling[key] = new ResolvedEntitlement(key, normalized, EntitlementConstants.Sources.ContractOverride);
            }
        }

        return ceiling;
    }

    /// <summary>
    /// Direction of "tighter": smaller for a numeric limit, false for a boolean capability. An
    /// unparseable request is never treated as a tightening — a value nobody can read must not
    /// become an entitlement.
    /// </summary>
    private static bool IsTighteningOrEqual(string key, string requested, string ceiling)
    {
        if (EntitlementConstants.Keys.IsNumericLimit(key))
        {
            return TryParseNumber(requested, out var requestedNumber)
                   && TryParseNumber(ceiling, out var ceilingNumber)
                   && requestedNumber <= ceilingNumber;
        }

        if (!bool.TryParse(requested, out var requestedFlag) || !bool.TryParse(ceiling, out var ceilingFlag))
        {
            return false;
        }

        // false ≤ true. Turning a capability OFF is a tightening; turning one ON that the plan does
        // not grant is a purchase.
        return !requestedFlag || ceilingFlag;
    }

    private static bool TryParseNumber(string value, out long parsed) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static string Normalize(string key, string value) =>
        TryNormalize(key, value, out var normalized) ? normalized : value;

    private static bool TryNormalize(string key, string value, out string normalized)
    {
        if (EntitlementConstants.Keys.IsNumericLimit(key))
        {
            if (TryParseNumber(value, out var number))
            {
                normalized = number.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }
        else if (bool.TryParse(value, out var flag))
        {
            normalized = flag ? "true" : "false";
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}
