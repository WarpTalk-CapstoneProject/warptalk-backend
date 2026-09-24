using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Records a platform admin's change to the plugin marketplace in the platform audit log, which the
/// workspace service owns.
/// </summary>
/// <remarks>
/// This service has no message bus, so it uses the synchronous transport auth and translation-room
/// already use (<c>AdminAuditService.RecordAdminAction</c>). It returns a <see cref="Result"/>
/// rather than being fire-and-forget because the caller records BEFORE it commits and abandons the
/// change when the record fails: that is the only ordering under which "every marketplace change is
/// audited" is true rather than aspirational.
/// </remarks>
public interface IAdminAuditRecorder
{
    /// <param name="action">An <c>AdminAuditPluginActions</c> value.</param>
    /// <param name="entityId">The plugin row's id.</param>
    /// <param name="beforeSummary">Redacted state before the change; never a secret.</param>
    /// <param name="afterSummary">Redacted state after the change; never a secret.</param>
    Task<Result> RecordPluginActionAsync(
        string action,
        Guid entityId,
        Guid actorId,
        IReadOnlyDictionary<string, string?>? beforeSummary,
        IReadOnlyDictionary<string, string?>? afterSummary,
        CancellationToken ct = default);
}
