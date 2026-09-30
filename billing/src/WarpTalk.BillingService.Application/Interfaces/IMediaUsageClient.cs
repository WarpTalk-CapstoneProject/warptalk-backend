using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>One UTC hour of one workspace's LiveKit usage, as translation-room reports it (GetMediaUsage RPC).</summary>
public sealed record MediaUsageHourRow(
    DateTime HourStart,
    Guid WorkspaceId,
    double RoomSeconds,
    double ParticipantSeconds,
    int RoomsStarted,
    int Recordings,
    long RecordingBytes);

/// <summary>LiveKit usage for the admin Providers page. Translation-room owns the rooms; billing only reads.</summary>
public interface IMediaUsageClient
{
    /// <summary>A failure (translation-room unreachable, window refused) is a Result, never an exception.</summary>
    Task<Result<IReadOnlyList<MediaUsageHourRow>>> GetHourlyAsync(DateTime from, DateTime to, CancellationToken ct = default);
}
