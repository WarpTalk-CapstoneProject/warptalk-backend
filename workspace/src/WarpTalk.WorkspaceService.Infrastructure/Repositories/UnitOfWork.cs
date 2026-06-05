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
    private IGenericRepository<WorkspaceDocument>? _workspaceDocumentRepository;
    private IGenericRepository<WorkspaceDocumentAccessPolicy>? _workspaceDocumentAccessPolicyRepository;
    private IGenericRepository<WorkspaceDocumentAudit>? _workspaceDocumentAuditRepository;
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

    public IGenericRepository<WorkspaceDocument> WorkspaceDocumentRepository =>
        _workspaceDocumentRepository ??= new GenericRepository<WorkspaceDocument>(_context);

    public IGenericRepository<WorkspaceDocumentAccessPolicy> WorkspaceDocumentAccessPolicyRepository =>
        _workspaceDocumentAccessPolicyRepository ??= new GenericRepository<WorkspaceDocumentAccessPolicy>(_context);

    public IGenericRepository<WorkspaceDocumentAudit> WorkspaceDocumentAuditRepository =>
        _workspaceDocumentAuditRepository ??= new GenericRepository<WorkspaceDocumentAudit>(_context);

    public IGenericRepository<T> Repository<T>() where T : class
    {
        _repositories ??= new Dictionary<Type, object>();
        var type = typeof(T);

        if (!_repositories.ContainsKey(type))
        {
            var repositoryType = typeof(GenericRepository<>);
            var repositoryInstance = Activator.CreateInstance(repositoryType.MakeGenericType(type), _context);
            _repositories.Add(type, repositoryInstance!);
        }

        return (IGenericRepository<T>)_repositories[type];
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _context.SaveChangesAsync(ct);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
