using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// What a provider's public status page last said (<c>/api/v2/status.json</c>). <paramref name="Indicator"/>
/// is statuspage.io's none | minor | major | critical | maintenance; <paramref name="Error"/> says why
/// the last poll failed, and the snapshot before it is kept.
/// </summary>
public sealed record ProviderStatusPageSnapshot(
    string Provider,
    string? Indicator,
    string? Description,
    DateTime? CheckedAt,
    string? Error);

/// <summary>Last status-page reading per provider, shared by every billing replica (Redis in production).</summary>
public interface IProviderStatusPageState
{
    Task<ProviderStatusPageSnapshot?> GetAsync(string provider, CancellationToken ct = default);

    Task SetAsync(ProviderStatusPageSnapshot snapshot, CancellationToken ct = default);
}
