using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// WT-327: recurring bookings. Creating one, cancelling one, and rolling its horizon forward.
///
/// The series is the only thing here that is new. Each occurrence is created by
/// <see cref="ITranslationRoomService.CreateTranslationRoomAsync"/> — the same method every
/// one-off room has always used — so nothing downstream of a room learned a new concept.
/// </summary>
public interface ITranslationRoomSeriesService
{
    /// <summary>
    /// Creates the series and materialises every occurrence inside the current horizon,
    /// returning the first one. Fails as a unit: if the first occurrence cannot be created —
    /// unsupported language, revoked host permission — no series row is left behind.
    /// </summary>
    Task<Result<CreateRecurringRoomResponse>> CreateSeriesAsync(
        CreateTranslationRoomRequest request,
        Guid hostId,
        CancellationToken ct = default);

    Task<Result<RecurrenceSummaryResponse>> GetSeriesAsync(Guid seriesId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Stops the series and cancels its FUTURE occurrences. Occurrences that already started,
    /// ended, or are happening right now are untouched — cancelling a booking cannot rewrite
    /// meetings that already happened.
    /// </summary>
    Task<Result<CancelSeriesResult>> CancelSeriesAsync(Guid seriesId, Guid hostId, CancellationToken ct = default);

    /// <summary>
    /// One materialisation pass over every series that still owes rooms. Called by
    /// RecurringSeriesMaterializationWorker; separated from it so the behaviour is testable
    /// without a host.
    /// </summary>
    Task<int> MaterializeDueOccurrencesAsync(CancellationToken ct = default);
}

/// <summary>WT-327: what a series cancel actually did, so the client can say so.</summary>
public record CancelSeriesResult(Guid SeriesId, int CancelledOccurrenceCount);
