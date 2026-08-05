namespace WarpTalk.WorkspaceService.Application.Models;

/// <summary>
/// WT-262: the slice of BillingService's feature-access contract that WorkspaceService enforces.
///
/// Deliberately not the full proto message — WorkspaceService should depend on the plan facts it
/// actually gates on, so adding a field here is a conscious decision to start enforcing it.
/// </summary>
/// <param name="HasActiveSubscription">
/// False when the workspace has no live paid plan. Quotas carried on this record describe a plan
/// that is not in force, so callers must not enforce them in that case.
/// </param>
/// <param name="MaxLanguages">The plan's <c>max_languages</c> column.</param>
public sealed record WorkspaceFeatureAccess(bool HasActiveSubscription, int MaxLanguages);
