using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private IGenericRepository<WorkspaceAdminAction>? _workspaceAdminActionRepository;
    private Dictionary<Type, object>? _repositories;

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

    public IGenericRepository<WorkspaceAdminAction> WorkspaceAdminActionRepository =>
        _workspaceAdminActionRepository ??= new GenericRepository<WorkspaceAdminAction>(_context);

    public IGenericRepository<T> Repository<T>() where T : class
    {
        _repositories ??= new Dictionary<Type, object>();
        var type = typeof(T);
        if (!_repositories.ContainsKey(type))
            _repositories.Add(type, new GenericRepository<T>(_context));
        return (IGenericRepository<T>)_repositories[type];
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
