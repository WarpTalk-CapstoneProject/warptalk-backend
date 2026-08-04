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

    // WorkspaceAdminAction is deliberately absent: it is the admin audit log, reached only
    // through the append-only IAdminAuditLogRepository (WT-210). Exposing it as a general
    // repository here handed every IUnitOfWork holder an Update()/Remove() on audit history.

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
