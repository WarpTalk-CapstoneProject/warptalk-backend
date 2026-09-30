using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

/// <summary>The values operators set on platform settings. Saved through the unit of work.</summary>
public interface IPlatformSettingValueRepository
{
    /// <summary>Every stored value at every scope, untracked. The whole table is small (one row per key and scope).</summary>
    Task<IReadOnlyList<PlatformSettingValue>> GetAllAsync(CancellationToken ct = default);

    /// <summary>One value, tracked; null when nothing is set at that scope.</summary>
    Task<PlatformSettingValue?> GetAsync(string key, string scopeType, string scopeId, CancellationToken ct = default);

    Task AddAsync(PlatformSettingValue value, CancellationToken ct = default);

    void Remove(PlatformSettingValue value);
}

/// <summary>
/// Append-and-read access to platform setting history. No update or delete, and the runtime role
/// has neither: the history is the record of who changed what.
/// </summary>
public interface IPlatformSettingChangeRepository
{
    Task AppendAsync(PlatformSettingChange change, CancellationToken ct = default);

    Task<PlatformSettingChange?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Newest first. <paramref name="key"/> null means every setting.</summary>
    Task<IReadOnlyList<PlatformSettingChange>> GetRecentAsync(string? key, int limit, CancellationToken ct = default);

    /// <summary>The latest change per key (for "last changed by/when" on the console).</summary>
    Task<IReadOnlyDictionary<string, PlatformSettingChange>> GetLatestPerKeyAsync(CancellationToken ct = default);
}
