using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// WT-263: a workspace's own self-service tightening of one entitlement — the fourth and last layer
/// of the resolution order.
///
/// It lives in the BILLING schema, not in the workspace's settings JSON, because the resolver is
/// the only code permitted to compute an entitlement and the resolver cannot enforce
/// "tighten but never loosen" against a value it cannot see. WorkspaceService still owns the
/// owner-facing setting; it pushes the chosen value here, and billing decides whether it is a legal
/// tightening before it becomes an entitlement.
///
/// Stored one row per (workspace, key) rather than as a JSON bag so a single setting can be cleared
/// without a read-modify-write race against a concurrent change to another setting.
/// </summary>
public sealed class WorkspaceEntitlementOverride
{
    public Guid WorkspaceId { get; set; }

    /// <summary>One of <see cref="Constants.EntitlementConstants.Keys"/>.</summary>
    public string EntitlementKey { get; set; } = string.Empty;

    /// <summary>
    /// The requested value, held as text so one table serves both numeric limits and boolean
    /// capabilities. The resolver parses it against the key's declared shape; a row that cannot be
    /// parsed is ignored rather than allowed to widen anything.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    public Guid? SetBy { get; set; }

    public DateTime UpdatedAt { get; set; }
}
