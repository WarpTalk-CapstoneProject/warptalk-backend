using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly WorkspaceDbContext _context;

    private IWorkspaceRepository? _workspaceRepository;
    private IWorkspaceMemberRepository? _workspaceMemberRepository;
    private IWorkspaceInvitationRepository? _workspaceInvitationRepository;
    private IWorkspaceDocumentRepository? _workspaceDocumentRepository;
    private IWorkspaceDocumentAccessPolicyRepository? _workspaceDocumentAccessPolicyRepository;
    private IWorkspaceDocumentAuditRepository? _workspaceDocumentAuditRepository;
    private IWorkspaceVerifiedDomainRepository? _workspaceVerifiedDomainRepository;
    private IWorkspaceOutboxMessageRepository? _workspaceOutboxMessageRepository;
    private IWorkspaceEntitlementSnapshotRepository? _workspaceEntitlementSnapshotRepository;

    public UnitOfWork(WorkspaceDbContext context)
    {
        _context = context;
    }

    public IWorkspaceRepository WorkspaceRepository =>
        _workspaceRepository ??= new WorkspaceRepository(_context);

    public IWorkspaceMemberRepository WorkspaceMemberRepository =>
        _workspaceMemberRepository ??= new WorkspaceMemberRepository(_context);

    public IWorkspaceInvitationRepository WorkspaceInvitationRepository =>
        _workspaceInvitationRepository ??= new WorkspaceInvitationRepository(_context);

    public IWorkspaceDocumentRepository WorkspaceDocumentRepository =>
        _workspaceDocumentRepository ??= new WorkspaceDocumentRepository(_context);

    public IWorkspaceDocumentAccessPolicyRepository WorkspaceDocumentAccessPolicyRepository =>
        _workspaceDocumentAccessPolicyRepository ??= new WorkspaceDocumentAccessPolicyRepository(_context);

    public IWorkspaceDocumentAuditRepository WorkspaceDocumentAuditRepository =>
        _workspaceDocumentAuditRepository ??= new WorkspaceDocumentAuditRepository(_context);

    public IWorkspaceVerifiedDomainRepository WorkspaceVerifiedDomainRepository =>
        _workspaceVerifiedDomainRepository ??= new WorkspaceVerifiedDomainRepository(_context);

    public IWorkspaceOutboxMessageRepository WorkspaceOutboxMessageRepository =>
        _workspaceOutboxMessageRepository ??= new WorkspaceOutboxMessageRepository(_context);

    public IWorkspaceEntitlementSnapshotRepository WorkspaceEntitlementSnapshotRepository =>
        _workspaceEntitlementSnapshotRepository ??= new WorkspaceEntitlementSnapshotRepository(_context);

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);

    /// <summary>
    /// PostgreSQL reports a unique-index rejection as SQLSTATE 23505 and names the index that
    /// rejected it. Both are matched: the state alone would also catch a violation of some
    /// unrelated index, and answering "yes" to that would dress a genuine bug up as a polite
    /// business error.
    ///
    /// Matched on SqlState and ConstraintName rather than message text, which is localised and
    /// version-dependent. Walks the inner-exception chain because EF wraps the provider
    /// exception in a DbUpdateException.
    /// </summary>
    public bool IsUniqueIndexViolation(Exception exception, string indexName)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                && string.Equals(pg.ConstraintName, indexName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
