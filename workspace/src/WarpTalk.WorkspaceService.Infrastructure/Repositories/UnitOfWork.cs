using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly WorkspaceDbContext _context;
    
    private IWorkspaceRepository? _workspaceRepository;
    private IWorkspaceMemberRepository? _workspaceMemberRepository;
    private IWorkspaceInvitationRepository? _workspaceInvitationRepository;
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
