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

    /// <summary>
    /// The booking, its rule, and every occurrence the caller may see.
    ///
    /// <paramref name="userEmail"/> is required and positional for the same reason it is on
    /// <see cref="ITranslationRoomService.GetTranslationRoomAsync"/>: entitlement to an occurrence
    /// is host OR participant OR invited-by-email. Until this took a caller at all, <c>[Authorize]</c>
    /// was the entire check and any authenticated user could read any workspace's series — its
    /// title, description, schedule and host — by guessing an id.
    ///
    /// A caller who may see nothing gets the same not-found as a series that does not exist.
    /// </summary>
    Task<Result<SeriesDetailResponse>> GetSeriesAsync(
        Guid seriesId,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Edits the BOOKING: the template, and every occurrence still ahead of it.
    ///
    /// Occurrences that already started keep what they ran with — rewriting a meeting that
    /// happened is not an edit. The rule itself (cadence, time of day, date range) is out of
    /// scope here; see <see cref="UpdateSeriesRequest"/>.
    /// </summary>
    Task<Result<UpdateSeriesResult>> UpdateSeriesAsync(
        Guid seriesId,
        Guid hostId,
        UpdateSeriesRequest request,
        CancellationToken ct = default);

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
