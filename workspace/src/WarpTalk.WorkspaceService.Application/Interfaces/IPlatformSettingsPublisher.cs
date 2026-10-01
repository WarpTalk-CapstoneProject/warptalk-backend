using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>The outcome of the last publish this process attempted.</summary>
public sealed record PlatformSettingsPublishState(long Version, DateTime? PublishedAt, bool Healthy, string? Error);

/// <summary>
/// Hands the stored platform settings to the services that read them
/// (WarpTalk.Shared.PlatformSettings.PlatformSettingsRedisKeys).
///
/// Always the WHOLE snapshot, stamped with <paramref name="version"/>: a publish carrying an older
/// version than the one already out there is dropped, so a slow replica re-publishing a snapshot it
/// read a second before someone's change cannot roll that change back.
/// </summary>
public interface IPlatformSettingsPublisher
{
    Task<bool> PublishAsync(IReadOnlyList<PlatformSettingValue> values, long version, CancellationToken ct = default);

    PlatformSettingsPublishState State { get; }
}
