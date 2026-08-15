using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>One sample of an instant PromQL vector.</summary>
public sealed record PlatformMetricSample(IReadOnlyDictionary<string, string> Labels, double Value)
{
    public string Label(string name) => Labels.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>An alert rule currently firing or pending, as the monitoring system sees it.</summary>
public sealed record PlatformAlert(
    string Name,
    string Severity,
    string State,
    string? Summary,
    DateTime? ActiveSince);

/// <summary>
/// Raised when the monitoring system could not be reached at all, as opposed to answering that
/// something is wrong. The caller must be able to tell those apart: rendering the first as the
/// second turns every monitoring restart into a reported platform outage.
/// </summary>
public sealed class PlatformMetricsUnavailableException : Exception
{
    public PlatformMetricsUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Read-only access to the platform's metrics store. Queries only — nothing here can silence an
/// alert or write a sample.
/// </summary>
public interface IPlatformMetricsSource
{
    /// <summary>
    /// Evaluates an instant PromQL query.
    /// </summary>
    /// <exception cref="PlatformMetricsUnavailableException">The store was unreachable.</exception>
    Task<IReadOnlyList<PlatformMetricSample>> QueryAsync(string expression, CancellationToken ct);

    /// <summary>
    /// Alerts in a firing or pending state right now.
    /// </summary>
    /// <exception cref="PlatformMetricsUnavailableException">The store was unreachable.</exception>
    Task<IReadOnlyList<PlatformAlert>> ActiveAlertsAsync(CancellationToken ct);
}
