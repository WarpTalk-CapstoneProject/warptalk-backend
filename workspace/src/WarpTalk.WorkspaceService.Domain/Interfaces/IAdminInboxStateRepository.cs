using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

/// <summary>Triage state of pending-work inbox items (G12). Saved through the unit of work.</summary>
public interface IAdminInboxStateRepository
{
    /// <summary>States of the given keys, untracked.</summary>
    Task<IReadOnlyList<AdminInboxItemState>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);

    /// <summary>The state of one key, tracked; null when the item was never triaged.</summary>
    Task<AdminInboxItemState?> GetAsync(string key, CancellationToken ct = default);

    Task AddAsync(AdminInboxItemState state, CancellationToken ct = default);
}

/// <summary>Append-and-read access to inbox notes (G12). No update or delete, and the runtime role has neither.</summary>
public interface IAdminInboxNoteRepository
{
    Task AppendAsync(AdminInboxNote note, CancellationToken ct = default);

    /// <summary>Oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<AdminInboxNote>> GetForItemAsync(string key, int limit, CancellationToken ct = default);

    /// <summary>How many notes each key has and when the latest was written.</summary>
    Task<IReadOnlyDictionary<string, (int Count, DateTime LastAt)>> SummarizeAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);
}
