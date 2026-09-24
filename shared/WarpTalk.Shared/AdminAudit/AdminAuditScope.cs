namespace WarpTalk.Shared.AdminAudit;

/// <summary>
/// The admin action the current request is performing, if it is performing one. Scoped per
/// request; armed by <see cref="AdminAuditActionFilter"/>, read by
/// <see cref="AdminAuditSaveChangesInterceptor"/>.
/// </summary>
public sealed class AdminAuditScope
{
    public AdminAuditInvocation? Current { get; private set; }

    public void Arm(AdminAuditInvocation invocation) => Current = invocation;
}

/// <summary>One audited endpoint call, from the filter's view of it.</summary>
public sealed class AdminAuditInvocation
{
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public required IReadOnlyList<Type> SubjectTypes { get; init; }
    public required Guid ActorId { get; init; }
    public required string CorrelationId { get; init; }
    public string? Reason { get; init; }
    public Guid? RouteEntityId { get; init; }
    public string? RouteEntityKey { get; init; }
    public Guid? RouteWorkspaceId { get; init; }
    public AdminAuditRequestMetadata Metadata { get; init; } = AdminAuditRequestMetadata.Empty;

    /// <summary>See <see cref="AdminAuditedAttribute.Aggregate"/>.</summary>
    public bool Aggregate { get; init; }

    private readonly List<AdminAuditRecord> _recorded = [];

    /// <summary>
    /// The entries already written for this call, one per subject row a save changed. A service
    /// that saves twice (retire the old rate card, then insert the new one) records both; a save
    /// that touches no subject (an outbox flush) records nothing.
    /// </summary>
    public IReadOnlyList<AdminAuditRecord> Recorded => _recorded;

    public bool HasRecorded => _recorded.Count > 0;

    public void AddRecorded(IEnumerable<AdminAuditRecord> records) => _recorded.AddRange(records);

    /// <summary>
    /// The entry a request without a tracked change still deserves: the subject from the route,
    /// no diff.
    /// </summary>
    public AdminAuditRecord ToRecord(string result, string? errorMessage, string correlationId) => new()
    {
        Action = Action,
        EntityType = EntityType,
        EntityId = RouteEntityId,
        EntityKey = RouteEntityKey,
        WorkspaceId = RouteWorkspaceId,
        ActorId = ActorId,
        Reason = Reason,
        Result = result,
        ErrorMessage = errorMessage,
        CorrelationId = correlationId,
        Metadata = Metadata,
    };
}
