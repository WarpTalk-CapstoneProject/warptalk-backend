using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// LiveKit usage (room minutes, participant minutes, egress recordings) per UTC hour and workspace,
/// for the admin Providers page. Server-to-server only: reached over the GetMediaUsage RPC, which
/// the billing service calls; there is no HTTP route to it.
/// </summary>
public interface IMediaUsageService
{
    /// <summary>The longest window one call may ask for.</summary>
    public const int MaxWindowDays = 120;

    /// <summary><see cref="ErrorCodes.ValidationError"/> for an empty, inverted or over-long window.</summary>
    Task<Result<IReadOnlyList<MediaUsageCalculator.HourRow>>> GetHourlyAsync(DateTime from, DateTime to, CancellationToken ct = default);
}
