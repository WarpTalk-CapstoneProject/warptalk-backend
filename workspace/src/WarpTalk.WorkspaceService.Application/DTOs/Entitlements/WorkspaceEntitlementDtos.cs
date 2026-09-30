using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Entitlements;

/// <summary>
/// The workspace's resolved entitlements, read from the local snapshot BillingService replicates
/// through <c>billing.entitlements_changed</c>. Read-only by construction: nothing here is
/// recomputed, only reported.
/// </summary>
/// <param name="IsKnown">
/// False when no snapshot has arrived yet (cold start). The list is then empty — the page must say
/// "not resolved yet" rather than render platform defaults, which would under-report a paying
/// workspace. See <c>WorkspaceEntitlements</c> for why cold start is not a denial.
/// </param>
/// <param name="PlanSlug">The plan billing resolved against, or null with no plan.</param>
/// <param name="HasActiveSubscription">False when plan values are not in force.</param>
/// <param name="ResolvedAt">When billing resolved the snapshot; null at cold start.</param>
public sealed record WorkspaceEntitlementsDto(
    bool IsKnown,
    string? PlanSlug,
    bool HasActiveSubscription,
    DateTime? ResolvedAt,
    IReadOnlyList<WorkspaceEntitlementDto> Entitlements);

/// <param name="Key">Wire key, e.g. <c>max_languages</c>.</param>
/// <param name="Kind"><c>flag</c> for a boolean capability, <c>limit</c> for a numeric one (a value of
/// 0 or less is not enforced, i.e. unlimited), <c>text</c> for anything else.</param>
/// <param name="Value">The resolved value as published (<c>"true"</c>, <c>"20"</c>).</param>
/// <param name="Source">Provenance as published: <c>platform_default</c>, <c>plan:&lt;slug&gt;</c>,
/// <c>contract_override</c> or <c>workspace_override</c>.</param>
/// <param name="Ceiling">For a <c>workspace_override</c>: the plan/contract value the owner tightened
/// against. Null otherwise, and null for snapshots published before billing carried it.</param>
/// <param name="CeilingSource">Provenance of <paramref name="Ceiling"/>.</param>
public sealed record WorkspaceEntitlementDto(
    string Key,
    string Kind,
    string Value,
    string Source,
    string? Ceiling,
    string? CeilingSource);
