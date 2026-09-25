using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceAdminNoteRepository : IWorkspaceAdminNoteRepository
{
    private readonly WorkspaceDbContext _context;

    public WorkspaceAdminNoteRepository(WorkspaceDbContext context)
    {
        _context = context;
    }

    public async Task AppendAsync(WorkspaceAdminNote note, CancellationToken ct = default)
    {
        await _context.WorkspaceAdminNotes.AddAsync(note, ct);
    }

    public Task<List<WorkspaceAdminNote>> GetForWorkspaceAsync(Guid workspaceId, int limit, CancellationToken ct = default) =>
        _context.WorkspaceAdminNotes
            .AsNoTracking()
            .Where(note => note.WorkspaceId == workspaceId)
            .OrderByDescending(note => note.CreatedAt)
            .ThenByDescending(note => note.Id)
            .Take(limit)
            .ToListAsync(ct);
}
