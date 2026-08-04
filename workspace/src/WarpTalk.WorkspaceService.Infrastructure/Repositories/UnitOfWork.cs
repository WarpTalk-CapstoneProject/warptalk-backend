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
    private IWorkspaceOutboxMessageRepository? _workspaceOutboxMessageRepository;

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

    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _currentTransaction;

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);

    public async Task BeginTransactionAsync(CancellationToken ct = default)
    {
        _currentTransaction = await _context.Database.BeginTransactionAsync(ct);
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.CommitAsync(ct);
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.RollbackAsync(ct);
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public void Dispose()
    {
        _currentTransaction?.Dispose();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
