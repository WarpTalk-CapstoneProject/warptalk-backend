using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Outbox;

/// <summary>
/// How many workspace outbox events exhausted their retries, and since when.
///
/// The admin Outbox page that listed these was retired; the replay endpoint still exists
/// (WorkspaceOutboxAdminController). Without a count somewhere an operator looks, a parked event
/// is a change that silently never reached the services that needed it.
/// </summary>
public sealed class WorkspaceOutboxDeadLetterReader(WorkspaceDbContext dbContext) : IOutboxDeadLetterReader
{
    public async Task<(long Count, DateTime? OldestAt)> ReadAsync(CancellationToken ct)
    {
        var parked = dbContext.WorkspaceOutboxMessages
            .AsNoTracking()
            .Where(message => message.DeadLetteredAt != null);

        var count = await parked.LongCountAsync(ct);
        var oldest = count == 0 ? null : await parked.MinAsync(message => message.DeadLetteredAt, ct);
        return (count, oldest);
    }
}
