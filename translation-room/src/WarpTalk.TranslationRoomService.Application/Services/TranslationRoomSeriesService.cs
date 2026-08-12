using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <summary>
/// WT-327: recurring bookings — "Daily at 08:00" — as a rule plus a rolling set of ordinary rooms.
///
/// WHY IT WORKS THIS WAY (the one decision everything else follows from)
///   Every downstream system in WarpTalk assumes one `translation_rooms` row is exactly one
///   meeting: billing meters per room, transcripts and artifacts hang off a room, occupancy and
///   seat caps count a room's participants, the reminder sweep reads a room's scheduled_at, and
///   the AI pipeline is summoned per room. Teaching a single row to mean "N meetings" would have
///   to be answered in each of those, and the tail is unbounded — "which occurrence is this
///   transcript for?" has no cheap answer.
///
///   So the series row is a BOOKING, never a meeting. A worker turns it into real rooms ahead of
///   time. The result is that not one downstream system changed.
/// </summary>
public class TranslationRoomSeriesService : ITranslationRoomSeriesService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomSeriesRepository _seriesRepository;
    private readonly ITranslationRoomService _translationRoomService;
    private readonly ILogger<TranslationRoomSeriesService> _logger;

    /// <summary>
    /// Injected so tests can drive the horizon without waiting for wall-clock time. Production
    /// always gets <c>() =&gt; DateTime.UtcNow</c>.
    /// </summary>
    private readonly Func<DateTime> _utcNow;

    public TranslationRoomSeriesService(
        IUnitOfWork unitOfWork,
        ITranslationRoomService translationRoomService,
        ILogger<TranslationRoomSeriesService> logger,
        Func<DateTime>? utcNow = null)
    {
        _unitOfWork = unitOfWork;
        _seriesRepository = unitOfWork.TranslationRoomSeriesRepository;
        _translationRoomService = translationRoomService;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Creation
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<Result<CreateRecurringRoomResponse>> CreateSeriesAsync(
        CreateTranslationRoomRequest request,
        Guid hostId,
        CancellationToken ct = default)
    {
        if (request.Recurrence is null)
            return Result.Failure<CreateRecurringRoomResponse>(
                RecurrenceMessages.RecurrenceRequired, ErrorCodes.ValidationError);

        var planned = RecurrencePlanner.Plan(request.Recurrence, _utcNow());
        if (!planned.IsSuccess)
            return Result.Failure<CreateRecurringRoomResponse>(planned.Error!, planned.ErrorCode);

        var plan = planned.Value!;

        // The dates this series will EVER have, and the subset inside today's horizon. Both are
        // computed before anything is written so a request that yields no occurrence at all
        // (an end date before the start) is refused rather than persisted as an inert series.
        var allDates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            plan.Type, plan.Interval, plan.StartDate, plan.EndDate,
            alreadyMaterializedThrough: null,
            horizonThrough: plan.EndDate,
            maxCount: RecurrenceLimits.MaxDurationDays + 1,
            plan.ByWeekdays, plan.ByMonthDay);

        if (allDates.Count == 0)
            return Result.Failure<CreateRecurringRoomResponse>(
                RecurrenceMessages.NoOccurrences, ErrorCodes.ValidationError);

        var horizonThrough = RecurrenceScheduleCalculator
            .LocalDateOf(_utcNow(), plan.TimeZone)
            .AddDays(RecurrenceLimits.HorizonDays);

        var initialDates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            plan.Type, plan.Interval, plan.StartDate, plan.EndDate,
            alreadyMaterializedThrough: null,
            horizonThrough: horizonThrough,
            maxCount: RecurrenceLimits.MaxOccurrencesPerPass,
            plan.ByWeekdays, plan.ByMonthDay);

        if (initialDates.Count == 0)
        {
            // The whole series sits beyond the 14-day horizon, which MONTHLY reaches routinely:
            // book "the 1st of every month" on the 5th and the first meeting is 27 days out.
            //
            // The first occurrence is therefore materialised whatever the horizon says. It is not
            // a horizon violation so much as the horizon's purpose — keeping a rolling window
            // full — not applying to the one room the caller is owed: the response carries a room
            // code the user shares, and a booking whose room does not exist for another month is
            // a booking they cannot invite anyone to. The worker picks up the rest normally,
            // because the watermark lands on this date and it only ever looks past it.
            initialDates = new[] { allDates[0] };
        }

        var series = BuildSeriesEntity(request, plan, hostId);

        // Atomic: a series whose first occurrence was refused (unsupported language, revoked
        // host permission) must not survive as a booking that produces rooms nobody asked for.
        await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            await _seriesRepository.AddAsync(series, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            var occurrences = new List<TranslationRoomDto>();
            // The first occurrence mints the code; every one after it answers to the same one, so
            // the booking has a single link to share for its whole life.
            string? sharedRoomCode = null;

            for (var index = 0; index < initialDates.Count; index++)
            {
                var created = await CreateOccurrenceAsync(
                    series, plan, initialDates[index], isFirst: index == 0, request.InvitedEmails, ct,
                    sharedRoomCode);

                if (!created.IsSuccess)
                {
                    await _unitOfWork.RollbackTransactionAsync(ct);
                    return Result.Failure<CreateRecurringRoomResponse>(created.Error!, created.ErrorCode);
                }

                sharedRoomCode ??= created.Value!.TranslationRoomCode;
                occurrences.Add(created.Value!);
            }

            series.MaterializedThroughLocalDate = initialDates[^1];
            if (RecurrenceScheduleCalculator.IsFullyMaterialized(series.MaterializedThroughLocalDate, series.EndsOnLocalDate))
            {
                series.Status = RecurrenceSeriesStatuses.Completed;
            }
            series.UpdatedAt = _utcNow();
            _seriesRepository.Update(series);
            await _unitOfWork.SaveChangesAsync(ct);

            await _unitOfWork.CommitTransactionAsync(ct);

            _logger.LogInformation(
                "WT-327: created {Type} series {SeriesId} for host {HostId} at {LocalTime} {TimeZone}; {Materialized} of {Total} occurrences materialised through {Through}.",
                series.RecurrenceType, series.Id, hostId, series.StartTimeLocal, series.TimeZone,
                occurrences.Count, allDates.Count, series.MaterializedThroughLocalDate);

            return Result.Success(new CreateRecurringRoomResponse(
                ToSummary(series),
                occurrences[0],
                occurrences.Count,
                allDates.Count));
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync(ct);
            _logger.LogError(ex, "WT-327: failed to create a recurring series for host {HostId}.", hostId);
            return Result.Failure<CreateRecurringRoomResponse>(
                RecurrenceMessages.CreateFailed, ErrorCodes.InternalServerError);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Read
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<Result<SeriesDetailResponse>> GetSeriesAsync(
        Guid seriesId,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default)
    {
        var series = await _seriesRepository.GetByIdAsync(seriesId, ct);
        if (series is null)
            return Result.Failure<SeriesDetailResponse>(RecurrenceMessages.SeriesNotFound, ErrorCodes.NotFound);

        var occurrences = await _translationRoomService.GetSeriesOccurrencesAsync(seriesId, userId, userEmail, ct);
        if (!occurrences.IsSuccess)
            return Result.Failure<SeriesDetailResponse>(occurrences.Error!, occurrences.ErrorCode);

        var visible = occurrences.Value!;

        // Authorization, and the whole reason this read takes a caller: the host always, and
        // anyone else only if they can see at least one of its meetings. Same not-found as a
        // series that does not exist — distinguishing the two would confirm the id to a prober,
        // which is most of the value of the id.
        if (series.HostId != userId && visible.Count == 0)
            return Result.Failure<SeriesDetailResponse>(RecurrenceMessages.SeriesNotFound, ErrorCodes.NotFound);

        return Result.Success(new SeriesDetailResponse(
            ToSummary(series),
            series.HostId,
            series.Title,
            series.Description,
            series.TranslationRoomType,
            series.SourceLanguage,
            LanguageHelper.ParseTargetLanguages(series.TargetLanguages),
            ReadInvitedEmails(series.InvitedEmails) ?? new List<string>(),
            visible,
            ResolveCurrentOccurrenceId(visible)));
    }

    /// <summary>
    /// The occurrence a "join this booking" action should land on.
    ///
    /// Live first — a meeting happening right now is the one the user means, even when the next
    /// scheduled slot is nearer on the clock than the one that overran. Otherwise the next one
    /// due. Null when the whole series is behind them, which is what turns a stable series link
    /// into an honest "nothing to join" rather than dropping someone into a finished meeting.
    /// </summary>
    private static Guid? ResolveCurrentOccurrenceId(List<TranslationRoomListItemDto> occurrences)
    {
        var live = occurrences.FirstOrDefault(o =>
            o.Status is RoomStatus.IN_PROGRESS or RoomStatus.PAUSED or RoomStatus.WAITING);
        if (live is not null) return live.Id;

        var now = DateTime.UtcNow;
        var next = occurrences
            .Where(o => o.Status == RoomStatus.SCHEDULED && (o.ScheduledAt ?? o.CreatedAt) >= now)
            .OrderBy(o => o.ScheduledAt ?? o.CreatedAt)
            .FirstOrDefault();

        return next?.Id;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Editing the booking
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<Result<UpdateSeriesResult>> UpdateSeriesAsync(
        Guid seriesId,
        Guid hostId,
        UpdateSeriesRequest request,
        CancellationToken ct = default)
    {
        var series = await _seriesRepository.GetByIdAsync(seriesId, ct);
        if (series is null)
            return Result.Failure<UpdateSeriesResult>(RecurrenceMessages.SeriesNotFound, ErrorCodes.NotFound);

        if (series.HostId != hostId)
            return Result.Failure<UpdateSeriesResult>(RecurrenceMessages.OnlyHostMayEdit, ErrorCodes.Forbidden);

        if (series.Status == RecurrenceSeriesStatuses.Cancelled)
            return Result.Failure<UpdateSeriesResult>(RecurrenceMessages.SeriesAlreadyCancelled, ErrorCodes.InvalidState);

        var now = _utcNow();

        // The template first, so occurrences the worker has not created yet are stamped from the
        // edited booking. Null means "leave it alone" on every field — a client that knows about
        // one of them cannot blank the rest.
        if (request.Title is { Length: > 0 }) series.Title = request.Title;
        if (request.Description is not null) series.Description = request.Description;
        if (request.MaxParticipants is > 0) series.MaxParticipants = request.MaxParticipants.Value;
        if (request.SourceLanguage is { Length: > 0 }) series.SourceLanguage = request.SourceLanguage;
        if (request.TargetLanguages is not null)
            series.TargetLanguages = LanguageHelper.SerializeTargetLanguages(request.TargetLanguages);
        if (request.Settings is not null) series.Settings = JsonSerializer.Serialize(request.Settings);
        if (request.InvitedEmails is not null) series.InvitedEmails = JsonSerializer.Serialize(request.InvitedEmails);

        series.UpdatedAt = now;
        series.UpdatedBy = hostId;
        _seriesRepository.Update(series);
        await _unitOfWork.SaveChangesAsync(ct);

        // Then the rooms that already exist and have not run yet. Same query the series cancel
        // uses — "future occurrences in a status that still accepts changes" is the same set for
        // both, and deriving it twice is how the two would come to disagree about what "future"
        // means.
        var futureOccurrences = await _seriesRepository.GetCancellableOccurrencesAsync(seriesId, now, ct);

        var updated = 0;
        foreach (var occurrence in futureOccurrences)
        {
            // Routed through the room service's own update so an occurrence is edited by exactly
            // the rules a one-off room is: language validation, the invited-email diff and its
            // notifications, and the target-language publish the AI pipeline reads. ScheduledAt is
            // deliberately never passed — the rule owns every occurrence's time.
            var result = await _translationRoomService.UpdateTranslationRoomSettingsAsync(
                occurrence.Id,
                hostId,
                new UpdateRoomSettingsRequest(
                    Title: request.Title,
                    Description: request.Description,
                    MaxParticipants: request.MaxParticipants,
                    ScheduledAt: null,
                    InvitedEmails: request.InvitedEmails,
                    Settings: request.Settings,
                    SourceLanguage: request.SourceLanguage,
                    TargetLanguages: request.TargetLanguages),
                ct);

            if (result.IsSuccess)
            {
                updated++;
            }
            else
            {
                // One occurrence that refuses the edit must not roll back a booking that is now
                // correct for every occurrence still to be created.
                _logger.LogWarning(
                    "WT-327: occurrence {RoomId} of series {SeriesId} could not be updated ({Error}); the booking itself was still changed.",
                    occurrence.Id, seriesId, result.Error);
            }
        }

        _logger.LogInformation(
            "WT-327: series {SeriesId} edited by host {HostId}; {Count} future occurrence(s) updated with it.",
            seriesId, hostId, updated);

        return Result.Success(new UpdateSeriesResult(seriesId, updated));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cancellation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancelling the SERIES stops future occurrences. Cancelling ONE occurrence is the existing
    /// per-room cancel and is deliberately left alone — it marks that room CANCELLED and the
    /// materialisation watermark guarantees the sweep never regenerates its date, so one skipped
    /// day does not kill the series and does not come back.
    ///
    /// Out of scope, stated rather than half-built: "this and all following", moving a series to
    /// a different hour, and changing the rule after creation. All three need edit semantics the
    /// UI has no controls for; a partial implementation would be worse than none.
    /// </summary>
    public async Task<Result<CancelSeriesResult>> CancelSeriesAsync(Guid seriesId, Guid hostId, CancellationToken ct = default)
    {
        var series = await _seriesRepository.GetByIdAsync(seriesId, ct);
        if (series is null)
            return Result.Failure<CancelSeriesResult>(RecurrenceMessages.SeriesNotFound, ErrorCodes.NotFound);

        if (series.HostId != hostId)
            return Result.Failure<CancelSeriesResult>(RecurrenceMessages.OnlyHostMayCancel, ErrorCodes.Forbidden);

        if (series.Status == RecurrenceSeriesStatuses.Cancelled)
        {
            // Idempotent: a double-click must not be an error.
            return Result.Success(new CancelSeriesResult(seriesId, 0));
        }

        var now = _utcNow();
        var futureOccurrences = await _seriesRepository.GetCancellableOccurrencesAsync(seriesId, now, ct);

        var cancelled = 0;
        foreach (var occurrence in futureOccurrences)
        {
            // Routed through the room service's own cancel so an occurrence is cancelled by
            // exactly the rules a one-off room is: participants disconnected, and the WT-314
            // session_ends publish that releases the AI pipeline's per-room bot. Bypassing it to
            // UPDATE the status column directly is how a cancelled room keeps billing LiveKit
            // connection minutes.
            var result = await _translationRoomService.CancelTranslationRoomAsync(occurrence.Id, hostId, ct);
            if (result.IsSuccess)
            {
                cancelled++;
            }
            else
            {
                // A single occurrence that refuses to cancel (it started a second ago) must not
                // strand the series in ACTIVE — the series stop below is the thing that matters.
                _logger.LogWarning(
                    "WT-327: occurrence {RoomId} of series {SeriesId} could not be cancelled ({Error}); the series is still being stopped.",
                    occurrence.Id, seriesId, result.Error);
            }
        }

        series.Status = RecurrenceSeriesStatuses.Cancelled;
        series.UpdatedAt = now;
        series.UpdatedBy = hostId;
        _seriesRepository.Update(series);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "WT-327: series {SeriesId} cancelled by host {HostId}; {Count} future occurrences cancelled with it.",
            seriesId, hostId, cancelled);

        return Result.Success(new CancelSeriesResult(seriesId, cancelled));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The rolling horizon
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extends every ACTIVE series up to <see cref="RecurrenceLimits.HorizonDays"/> ahead.
    ///
    /// This is what keeps a 30-day daily booking alive: creation materialises the first two
    /// weeks, and each pass of this adds the days that have newly come inside the horizon. A
    /// series is marked COMPLETED the moment its watermark reaches its end date, so a finished
    /// or abandoned series is filtered out in SQL and never examined again.
    /// </summary>
    public async Task<int> MaterializeDueOccurrencesAsync(CancellationToken ct = default)
    {
        var due = await _seriesRepository.GetSeriesNeedingMaterializationAsync(
            limit: RecurrenceLimits.MaxSeriesPerSweep, ct);

        var created = 0;

        foreach (var series in due)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                created += await MaterializeOneAsync(series, ct);
            }
            catch (Exception ex)
            {
                // One bad series — an unresolvable time zone after a tzdata change, a workspace
                // that revoked the host's permission — must not stop the sweep for every other.
                _logger.LogError(ex, "WT-327: failed to materialise series {SeriesId}.", series.Id);
            }
        }

        return created;
    }

    private async Task<int> MaterializeOneAsync(TranslationRoomSeries series, CancellationToken ct)
    {
        var timeZone = RecurrenceScheduleCalculator.ResolveTimeZone(series.TimeZone);
        if (timeZone is null)
        {
            _logger.LogError(
                "WT-327: series {SeriesId} names time zone '{TimeZone}', which this host cannot resolve. No further occurrences will be created for it.",
                series.Id, series.TimeZone);
            return 0;
        }

        var now = _utcNow();
        var horizonThrough = RecurrenceScheduleCalculator.LocalDateOf(now, timeZone)
            .AddDays(RecurrenceLimits.HorizonDays);

        var byWeekdays = RecurrenceRuleJson.ReadWeekdays(series.RecurrenceByWeekdays);

        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            series.RecurrenceType,
            series.RecurrenceInterval,
            series.StartsOnLocalDate,
            series.EndsOnLocalDate,
            series.MaterializedThroughLocalDate,
            horizonThrough,
            RecurrenceLimits.MaxOccurrencesPerPass,
            byWeekdays,
            series.RecurrenceByMonthDay);

        if (dates.Count == 0)
        {
            // Nothing to do now. If the watermark has reached the end date the series is
            // finished for good — retire it so the sweep stops looking at it.
            if (RecurrenceScheduleCalculator.IsFullyMaterialized(series.MaterializedThroughLocalDate, series.EndsOnLocalDate))
            {
                series.Status = RecurrenceSeriesStatuses.Completed;
                series.UpdatedAt = now;
                _seriesRepository.Update(series);
                await _unitOfWork.SaveChangesAsync(ct);
            }
            return 0;
        }

        var plan = new RecurrencePlan(
            series.RecurrenceType, series.RecurrenceInterval, series.StartTimeLocal,
            timeZone, series.StartsOnLocalDate, series.EndsOnLocalDate,
            byWeekdays, series.RecurrenceByMonthDay);

        var invitedEmails = ReadInvitedEmails(series.InvitedEmails);

        // Read the booking's code off an occurrence that already exists, so the ones this sweep
        // creates answer to it too. Read rather than stored on the series row, which is what keeps
        // "one code per booking" a code change instead of a migration against a live table.
        var existingOccurrence = await _unitOfWork.TranslationRoomRepository
            .FirstOrDefaultAsync(room => room.SeriesId == series.Id, ct: ct);
        var sharedRoomCode = existingOccurrence?.TranslationRoomCode;

        var created = 0;

        foreach (var date in dates)
        {
            var result = await CreateOccurrenceAsync(
                series, plan, date, isFirst: false, invitedEmails, ct, sharedRoomCode);
            if (!result.IsSuccess)
            {
                // Stop at the first refusal and leave the watermark where it is, so the next
                // sweep retries this same date. Advancing past a date we failed to create would
                // silently drop a meeting the host booked.
                _logger.LogWarning(
                    "WT-327: series {SeriesId} could not materialise {Date} ({Error}); stopping this pass and retrying next sweep.",
                    series.Id, date, result.Error);
                break;
            }

            created++;
            series.MaterializedThroughLocalDate = date;
        }

        if (created > 0)
        {
            if (RecurrenceScheduleCalculator.IsFullyMaterialized(series.MaterializedThroughLocalDate, series.EndsOnLocalDate))
            {
                series.Status = RecurrenceSeriesStatuses.Completed;
            }
            series.UpdatedAt = now;
            _seriesRepository.Update(series);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation(
                "WT-327: series {SeriesId} materialised {Count} occurrence(s) through {Through}.",
                series.Id, created, series.MaterializedThroughLocalDate);
        }

        return created;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internals
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and persists ONE occurrence through the ordinary room-creation path.
    ///
    /// The status is implicit and important: because <c>ScheduledAt</c> is always supplied, the
    /// room is created SCHEDULED — which is exactly what ReminderNotificationWorker filters on,
    /// so occurrences get their T-10min and T-1min reminders like any other booked meeting.
    /// (Known interaction, NOT fixed here: WT-326 — OpenWaitingRoomAsync flips SCHEDULED→WAITING
    /// with no time gate, which removes a room from that sweep. It affects an occurrence exactly
    /// as it affects a one-off room, no more.)
    ///
    /// Translation is NOT started here and must not be: a recurring room is still started by its
    /// host, by hand. WT-183 added auto-start, WT-248 reversed it, and the invariant is pinned by
    /// warptalk-web/scripts/check-room-detail-thread-flow.mjs.
    /// </summary>
    private async Task<Result<TranslationRoomDto>> CreateOccurrenceAsync(
        TranslationRoomSeries series,
        RecurrencePlan plan,
        DateOnly localDate,
        bool isFirst,
        List<string>? invitedEmails,
        CancellationToken ct,
        string? sharedRoomCode = null)
    {
        var scheduledAtUtc = RecurrenceScheduleCalculator.ToUtcInstant(localDate, plan.StartTimeLocal, plan.TimeZone);

        var occurrenceRequest = new CreateTranslationRoomRequest(
            WorkspaceId: series.WorkspaceId,
            Title: series.Title,
            Description: series.Description,
            TranslationRoomType: series.TranslationRoomType,
            MaxParticipants: series.MaxParticipants > 0 ? series.MaxParticipants : null,
            SourceLanguage: series.SourceLanguage,
            TargetLanguages: LanguageHelper.ParseTargetLanguages(series.TargetLanguages),
            Settings: ReadSettingsRequest(series.Settings),
            ScheduledAt: scheduledAtUtc,
            InvitedEmails: invitedEmails,
            Recurrence: null);

        return await _translationRoomService.CreateTranslationRoomAsync(
            occurrenceRequest,
            series.HostId,
            ct,
            new SeriesOccurrenceContext(series.Id, localDate, SendInvitationEmails: isFirst, SharedRoomCode: sharedRoomCode));
    }

    private TranslationRoomSeries BuildSeriesEntity(CreateTranslationRoomRequest request, RecurrencePlan plan, Guid hostId)
    {
        var now = _utcNow();
        var roomType = TranslationRoomTypes.Normalize(request.TranslationRoomType) ?? TranslationRoomTypes.Event;

        return new TranslationRoomSeries
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId!.Value,
            HostId = hostId,
            RecurrenceType = plan.Type,
            RecurrenceInterval = plan.Interval,
            // The planner has already resolved these to exactly one shape per cadence — weekdays
            // for WEEKLY, a day of the month for MONTHLY, neither for DAILY — so what is stored is
            // never "whatever the client happened to send".
            RecurrenceByWeekdays = RecurrenceRuleJson.WriteWeekdays(plan.ByWeekdays),
            RecurrenceByMonthDay = plan.ByMonthDay,
            StartTimeLocal = plan.StartTimeLocal,
            TimeZone = plan.TimeZone.Id,
            StartsOnLocalDate = plan.StartDate,
            EndsOnLocalDate = plan.EndDate,
            Status = RecurrenceSeriesStatuses.Active,
            MaterializedThroughLocalDate = null,
            Title = request.Title,
            Description = request.Description,
            TranslationRoomType = roomType,
            // 0 means "let the meeting type decide", preserved as-is so an occurrence inherits
            // the type's seat count rather than a number frozen at series-creation time.
            MaxParticipants = request.MaxParticipants is > 0 ? request.MaxParticipants.Value : 0,
            // Language resolution (user defaults, normalisation, the supported-language check)
            // belongs to CreateTranslationRoomAsync and runs per occurrence. Storing the raw
            // request values keeps exactly one place that decides what a room's languages are.
            SourceLanguage = request.SourceLanguage ?? string.Empty,
            TargetLanguages = LanguageHelper.SerializeTargetLanguages(request.TargetLanguages ?? new List<string>()),
            Settings = JsonSerializer.Serialize(request.Settings ?? new RoomSettingsRequest()),
            InvitedEmails = JsonSerializer.Serialize(request.InvitedEmails ?? new List<string>()),
            CreatedAt = now,
            CreatedBy = hostId,
            UpdatedAt = now,
            UpdatedBy = hostId
        };
    }

    private static RoomSettingsRequest? ReadSettingsRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<RoomSettingsRequest>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            // Malformed blob — fall back to "caller stated nothing", which lets the meeting
            // type seed every value, rather than failing a whole day's occurrence over it.
            return null;
        }
    }

    private static List<string>? ReadInvitedEmails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var emails = JsonSerializer.Deserialize<List<string>>(json);
            return emails is { Count: > 0 } ? emails : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RecurrenceSummaryResponse ToSummary(TranslationRoomSeries series) =>
        new(
            series.Id,
            series.RecurrenceType,
            series.StartTimeLocal.ToString("HH:mm", CultureInfo.InvariantCulture),
            series.TimeZone,
            series.StartsOnLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            series.EndsOnLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            series.Status,
            series.RecurrenceInterval,
            RecurrenceRuleJson.ReadWeekdays(series.RecurrenceByWeekdays)?.ToList(),
            series.RecurrenceByMonthDay);
}
