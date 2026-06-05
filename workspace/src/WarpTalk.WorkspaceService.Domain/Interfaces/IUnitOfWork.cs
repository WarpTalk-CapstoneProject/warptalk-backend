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
    IGenericRepository<WorkspaceDocument> WorkspaceDocumentRepository { get; }
    IGenericRepository<WorkspaceDocumentAccessPolicy> WorkspaceDocumentAccessPolicyRepository { get; }
    IGenericRepository<WorkspaceDocumentAudit> WorkspaceDocumentAuditRepository { get; }
    IGenericRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
