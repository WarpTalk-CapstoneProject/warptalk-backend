namespace WarpTalk.Shared.AdminAudit;

/// <summary>
/// Puts an endpoint's writes into the platform audit log without the endpoint's service having to
/// say so line by line.
/// </summary>
/// <remarks>
/// <para>
/// When a platform administrator calls an endpoint carrying this attribute,
/// <see cref="AdminAuditActionFilter"/> arms the request's <see cref="AdminAuditScope"/>. Every
/// <c>SaveChanges</c> that touches one of the <see cref="SubjectTypes"/> is then held by
/// <see cref="AdminAuditSaveChangesInterceptor"/> until the change has been recorded — with the
/// before/after diff read from the change tracker — and is refused if it cannot be. So the
/// ordering the hand-written admin paths use ("record, then commit, or do not commit") holds here
/// too, and a later edit to the service cannot quietly skip it.
/// </para>
/// <para>
/// A request that ends in an error is recorded as <c>failed</c> with the error the caller saw. A
/// workspace owner calling the same route is not a platform admin action and records nothing here.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AdminAuditedAttribute : Attribute
{
    public AdminAuditedAttribute(string action, string entityType, params Type[] subjectTypes)
    {
        Action = action;
        EntityType = entityType;
        SubjectTypes = subjectTypes;
    }

    /// <summary>The verb recorded, e.g. <c>plan.updated</c>. At most 30 characters.</summary>
    public string Action { get; }

    /// <summary>An <c>AdminAuditEntityTypes</c> value.</summary>
    public string EntityType { get; }

    /// <summary>
    /// Entity classes whose insert, update or delete IS the action. Other rows the same save
    /// writes — outbox messages, idempotency keys — are not the subject and are not diffed.
    /// </summary>
    public IReadOnlyList<Type> SubjectTypes { get; }

    /// <summary>
    /// Route value holding the subject's id when the change tracker cannot supply it (a failed
    /// request never reaches a save). A GUID becomes the entity id; anything else the entity key.
    /// </summary>
    public string? EntityRouteKey { get; set; }

    /// <summary>Route value holding the workspace the action was taken on, when there is one.</summary>
    public string WorkspaceRouteKey { get; set; } = "workspaceId";

    /// <summary>
    /// One entry per save, counting the rows it added, changed and removed, instead of one entry
    /// per row — for a bulk import, where a row-by-row account would bury everything else.
    /// </summary>
    public bool Aggregate { get; set; }
}
