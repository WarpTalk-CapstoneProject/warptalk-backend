using System.Diagnostics.Metrics;

namespace WarpTalk.WorkspaceService.Infrastructure.Outbox;

public static class WorkspaceOutboxMetrics
{
    private static readonly Meter Meter = new("warptalk-workspace");

    internal static readonly Counter<long> Published = Meter.CreateCounter<long>(
        "warptalk.workspace.outbox.published");

    internal static readonly Counter<long> Failed = Meter.CreateCounter<long>(
        "warptalk.workspace.outbox.failed");

    internal static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "warptalk.workspace.outbox.dead_lettered");

    public static readonly Counter<long> Replayed = Meter.CreateCounter<long>(
        "warptalk.workspace.outbox.replayed");
}
