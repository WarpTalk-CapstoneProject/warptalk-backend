using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IWorkspaceRepository WorkspaceRepository { get; }
    IWorkspaceMemberRepository WorkspaceMemberRepository { get; }
    IWorkspaceInvitationRepository WorkspaceInvitationRepository { get; }
    IWorkspaceDocumentRepository WorkspaceDocumentRepository { get; }
    IWorkspaceDocumentAccessPolicyRepository WorkspaceDocumentAccessPolicyRepository { get; }
    IWorkspaceDocumentAuditRepository WorkspaceDocumentAuditRepository { get; }
    IWorkspaceVerifiedDomainRepository WorkspaceVerifiedDomainRepository { get; }
    IWorkspaceOutboxMessageRepository WorkspaceOutboxMessageRepository { get; }

    /// <summary>WT-263: the local entitlement snapshot enforcement reads instead of calling billing.</summary>
    IWorkspaceEntitlementSnapshotRepository WorkspaceEntitlementSnapshotRepository { get; }

    // WorkspaceAdminAction is deliberately absent: it is the admin audit log, reached only
    // through the append-only IAdminAuditLogRepository (WT-210). Exposing it as a general
    // repository here handed every IUnitOfWork holder an Update()/Remove() on audit history.

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="exception"/> is the database refusing a write because it would
    /// duplicate a value the named unique index already holds — as opposed to any other failure,
    /// which must still surface as an error.
    ///
    /// Exists so a service can tell "somebody else got there first" apart from "the database is
    /// broken" without knowing which database it is talking to. The vendor-specific detection
    /// lives in the implementation; the caller only names the index whose rule it expects.
    /// </summary>
    bool IsUniqueIndexViolation(Exception exception, string indexName);
}
