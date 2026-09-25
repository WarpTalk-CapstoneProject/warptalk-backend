using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <summary>
/// rec-loss: one recording row per LiveKit egress, from the moment it starts to the moment it ends.
///
/// This used to be <c>RecordingCompletedEventProcessor</c>, and the only row it ever wrote was the
/// COMPLETED one, created when LiveKit reported a file. Every other ending — egress failed, aborted,
/// ran out of plan minutes, uploaded nothing — left no row, so the meeting record page could not
/// tell "nobody pressed Record" from "the recording was lost". It showed nothing for both.
///
/// The state machine, per <c>ProviderArtifactId == EgressId</c>:
/// <code>
///   (none)     --started-->    PROCESSING
///   (none)     --completed-->  COMPLETED
///   (none)     --failed-->     FAILED
///   PROCESSING --completed-->  COMPLETED
///   PROCESSING --failed-->     FAILED
///   FAILED     --completed-->  COMPLETED   a real file beats an earlier failure
///   COMPLETED  --failed-->     (no-op)     never downgrade a recording that has a file
///   anything   --started-->    (no-op)     start carries nothing a later event lacks
///   X          --X-->          (no-op)     duplicate delivery
/// </code>
/// Events can arrive in any order: meeting-service publishes Started from the request path and
/// Completed/Failed from the webhook or the sweep, and the stream consumer may run on several
/// replicas. So every event must be able to CREATE the row, and no event may assume one exists.
///
/// WHERE THE FAILURE REASON GOES (WT-824). Not <c>Content</c> — the download endpoint serves that
/// as the artifact's body, so a reason there would "download" as the recording itself. It has its
/// own column, <c>failure_reason</c>, written by <see cref="DescribeFailure"/>: the host-safe
/// sentence plus LiveKit's status and error, with URLs redacted and the length bounded. rec-loss
/// left it in the log only, and production then made two recordings, both FAILED, whose reasons
/// the next deploy deleted along with the logs — LiveKit's egress runs in LiveKit Cloud, so there
/// was nowhere else to look.
/// </summary>
public sealed class RecordingLifecycleEventProcessor : IRecordingLifecycleEventProcessor
{
    private static readonly string RecordingType = ArtifactType.OPTIONAL_RECORDING.ToString();
    internal static readonly string ProcessingStatus = ArtifactStatus.Processing.ToString().ToUpperInvariant();
    internal static readonly string CompletedStatus = ArtifactStatus.Completed.ToString().ToUpperInvariant();
    internal static readonly string FailedStatus = ArtifactStatus.Failed.ToString().ToUpperInvariant();

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<RecordingLifecycleEventProcessor> _logger;

    public RecordingLifecycleEventProcessor(
        IUnitOfWork unitOfWork,
        ILogger<RecordingLifecycleEventProcessor> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<bool>> ProcessAsync(
        EventEnvelope<MeetingRecordingStartedEventPayload> envelope,
        CancellationToken ct = default)
    {
        var invalid = Validate(envelope, MeetingEventTypes.RecordingStarted);
        if (invalid != null) return invalid;

        var payload = envelope.Payload;
        if (payload.TranslationRoomId == Guid.Empty || string.IsNullOrWhiteSpace(payload.EgressId))
        {
            return Result.Failure<bool>(
                "Recording started event is missing translation_room_id or egress_id",
                ErrorCodes.ValidationError);
        }

        var occurredAt = envelope.OccurredAt.ToUniversalTime();

        return await ApplyAsync(
            payload.EgressId,
            create: () =>
            {
                var artifact = NewRecording(payload.TranslationRoomId, payload.EgressId, occurredAt, ProcessingStatus);
                // Raw media is what this row will hold once it completes, and the consent hold has
                // to be in place from the first moment the row is visible — not added later.
                artifact.ContainsRawAudio = true;
                artifact.ContainsRawVideo = true;
                artifact.RecordingStartedAt = payload.StartedAt.ToUniversalTime();
                return artifact;
            },
            transition: existing =>
            {
                // Started never moves a row: whatever is already there was written by an event
                // that knows at least as much — or it is this same Started, delivered twice.
                _logger.LogInformation(
                    "Recording artifact for LiveKit egress {EgressId} already exists ({Status}); ignoring started event",
                    payload.EgressId, existing.Status);
                return false;
            },
            occurredAt,
            ct);
    }

    public async Task<Result<bool>> ProcessAsync(
        EventEnvelope<MeetingRecordingCompletedEventPayload> envelope,
        CancellationToken ct = default)
    {
        var invalid = Validate(envelope, MeetingEventTypes.RecordingCompleted);
        if (invalid != null) return invalid;

        var payload = envelope.Payload;
        if (payload.TranslationRoomId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.EgressId) ||
            string.IsNullOrWhiteSpace(payload.FileUrl))
        {
            return Result.Failure<bool>(
                "Recording event is missing translation_room_id, egress_id, or file_url",
                ErrorCodes.ValidationError);
        }

        var occurredAt = envelope.OccurredAt.ToUniversalTime();

        return await ApplyAsync(
            payload.EgressId,
            create: () =>
            {
                var artifact = NewRecording(payload.TranslationRoomId, payload.EgressId, occurredAt, CompletedStatus);
                ApplyFile(artifact, payload);
                return artifact;
            },
            transition: existing =>
            {
                if (IsStatus(existing, CompletedStatus))
                {
                    _logger.LogInformation(
                        "Recording artifact for LiveKit egress {EgressId} is already COMPLETED; treating delivery as idempotent",
                        payload.EgressId);
                    return false;
                }

                if (IsStatus(existing, FailedStatus))
                {
                    // A file LiveKit actually uploaded outranks an earlier "failed" — the sweep can
                    // give up on an egress whose webhook was only late.
                    _logger.LogWarning(
                        "Recording artifact for LiveKit egress {EgressId} was FAILED; a completed file arrived later and replaces it",
                        payload.EgressId);
                }
                else if (!IsStatus(existing, ProcessingStatus))
                {
                    // EXPIRED, or anything a later writer invents: not a recording lifecycle state
                    // this processor owns, so it is not this processor's to overwrite.
                    _logger.LogWarning(
                        "Recording artifact for LiveKit egress {EgressId} has status {Status}; ignoring completed event",
                        payload.EgressId, existing.Status);
                    return false;
                }

                ApplyFile(existing, payload);
                existing.Status = CompletedStatus;
                // The file exists; whatever an earlier "failed" said about it is no longer true.
                existing.FailureReason = null;
                return true;
            },
            occurredAt,
            ct);
    }

    public async Task<Result<bool>> ProcessAsync(
        EventEnvelope<MeetingRecordingFailedEventPayload> envelope,
        CancellationToken ct = default)
    {
        var invalid = Validate(envelope, MeetingEventTypes.RecordingFailed);
        if (invalid != null) return invalid;

        var payload = envelope.Payload;
        if (payload.TranslationRoomId == Guid.Empty || string.IsNullOrWhiteSpace(payload.EgressId))
        {
            return Result.Failure<bool>(
                "Recording failed event is missing translation_room_id or egress_id",
                ErrorCodes.ValidationError);
        }

        // Logged once per delivery, whatever the row does next: this is the only place the reason
        // survives (see the class comment), and LiveKit's error is free text that belongs in logs,
        // never in a response.
        _logger.LogWarning(
            "Recording for LiveKit egress {EgressId} in room {TranslationRoomId} failed: {Reason} (LiveKit status {LiveKitStatus}, error {LiveKitError})",
            payload.EgressId, payload.TranslationRoomId, payload.Reason, payload.LiveKitStatus, payload.LiveKitError);

        var occurredAt = envelope.OccurredAt.ToUniversalTime();
        var failureReason = DescribeFailure(payload.Reason, payload.LiveKitStatus, payload.LiveKitError);

        return await ApplyAsync(
            payload.EgressId,
            create: () =>
            {
                var artifact = NewRecording(payload.TranslationRoomId, payload.EgressId, occurredAt, FailedStatus);
                artifact.ContainsRawAudio = true;
                artifact.ContainsRawVideo = true;
                artifact.FailureReason = failureReason;
                return artifact;
            },
            transition: existing =>
            {
                if (IsStatus(existing, ProcessingStatus))
                {
                    existing.Status = FailedStatus;
                    existing.FailureReason = failureReason;
                    return true;
                }

                if (IsStatus(existing, CompletedStatus))
                {
                    // Never downgrade. A row with a file is a recording that exists; a failure
                    // reported after it (a sweep racing the webhook) is wrong about this egress.
                    _logger.LogWarning(
                        "Recording artifact for LiveKit egress {EgressId} is already COMPLETED; ignoring failed event",
                        payload.EgressId);
                    return false;
                }

                _logger.LogInformation(
                    "Recording artifact for LiveKit egress {EgressId} has status {Status}; ignoring failed event",
                    payload.EgressId, existing.Status);
                return false;
            },
            occurredAt,
            ct);
    }

    /// <summary>
    /// Read the egress's row; create it if absent, otherwise run the transition. <paramref name="transition"/>
    /// returns whether it changed anything, and only then is the row saved.
    ///
    /// THE CREATE RACE. <c>translation_room_artifacts_provider_artifact_id_key</c> is unique, and two
    /// consumers (replicas, or a reclaimed pending entry) can both read "no row" for one egress —
    /// Started and Completed published a second apart is the ordinary case, not an exotic one. The
    /// loser's insert fails; it then detaches its own pending insert (it would otherwise be retried
    /// by the next SaveChanges on this context), reads the winner's row, and applies its event as a
    /// transition. If there is still no row, the failure was not this race and is rethrown.
    /// </summary>
    private async Task<Result<bool>> ApplyAsync(
        string egressId,
        Func<TranslationRoomArtifact> create,
        Func<TranslationRoomArtifact, bool> transition,
        DateTime occurredAt,
        CancellationToken ct)
    {
        var repository = _unitOfWork.TranslationRoomArtifactRepository;
        var existing = await FindAsync(egressId, ct);

        if (existing == null)
        {
            var artifact = create();

            // WT-826: a recording that first appears after the room ended and auto-shared its
            // record is already released — that publish counted as the host's release. A row that
            // existed at the end was released there (TranslationRoomService.EndTranslationRoomAsync);
            // this is the one that arrived too late for that, e.g. a Completed with no Started.
            if (artifact.ConsentRequired && await IsReleasedByAutoShareAsync(artifact.TranslationRoomId, ct))
                artifact.ConsentRequired = false;

            await repository.AddAsync(artifact, ct);

            try
            {
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Success(true);
            }
            catch (DbUpdateException ex)
            {
                // Remove on an entity EF is still tracking as Added detaches it; nothing is deleted.
                repository.Remove(artifact);

                existing = await FindAsync(egressId, ct);
                if (existing == null)
                    throw;

                _logger.LogInformation(
                    ex,
                    "Recording artifact for LiveKit egress {EgressId} was created concurrently; applying event to the existing row",
                    egressId);
            }
        }

        if (existing.DeletedAt != null)
        {
            // Someone deleted this recording on purpose. A late lifecycle event must not bring it back.
            _logger.LogInformation(
                "Recording artifact for LiveKit egress {EgressId} is deleted; ignoring event",
                egressId);
            return Result.Success(false);
        }

        if (!transition(existing))
            return Result.Success(false);

        existing.UpdatedAt = occurredAt;
        repository.Update(existing);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(true);
    }

    /// <summary>
    /// Whether the room this recording belongs to has already been published by its own
    /// auto-share setting. Fails toward HOLDING: an unreadable room keeps the recording behind the
    /// host's release, which is the state every recording was in before WT-826.
    /// </summary>
    private async Task<bool> IsReleasedByAutoShareAsync(Guid translationRoomId, CancellationToken ct)
    {
        try
        {
            var room = await _unitOfWork.TranslationRoomRepository.GetByIdAsync(translationRoomId, ct);
            return room != null && RecordAutoShare.ReleasesRecordingHold(room, RecordAutoShare.Read(room.Settings));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Could not read room {RoomId} to decide the recording consent hold; keeping it held",
                translationRoomId);
            return false;
        }
    }

    private Task<TranslationRoomArtifact?> FindAsync(string egressId, CancellationToken ct) =>
        _unitOfWork.TranslationRoomArtifactRepository.FirstOrDefaultAsync(
            artifact => artifact.ProviderArtifactId == egressId,
            ct: ct);

    private static Result<bool>? Validate<TPayload>(EventEnvelope<TPayload> envelope, string expectedType)
    {
        if (envelope.EventType != expectedType ||
            envelope.SchemaVersion != DomainEventEnvelope.CurrentSchemaVersion)
        {
            return Result.Failure<bool>(
                $"Unsupported recording event {envelope.EventType}@{envelope.SchemaVersion}",
                ErrorCodes.ValidationError);
        }

        // An envelope with no "payload" key deserialises with Payload null; without this it would
        // throw NullReferenceException out of the handler instead of being dead-lettered.
        if (envelope.Payload is null)
            return Result.Failure<bool>("Recording event has no payload", ErrorCodes.ValidationError);

        return null;
    }

    private static TranslationRoomArtifact NewRecording(
        Guid translationRoomId,
        string egressId,
        DateTime occurredAt,
        string status) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = translationRoomId,
            ProviderArtifactId = egressId,
            ArtifactType = RecordingType,
            ConsentRequired = true,
            Status = status,
            CreatedAt = occurredAt,
            // Equal to CreatedAt on insert, so NULL keeps meaning "predates the column" rather than
            // "never updated". A later lifecycle event moves it.
            UpdatedAt = occurredAt
        };

    private static void ApplyFile(TranslationRoomArtifact artifact, MeetingRecordingCompletedEventPayload payload)
    {
        artifact.FileUrl = payload.FileUrl;
        artifact.FileFormat = payload.FileFormat;
        artifact.FileSizeBytes = payload.FileSizeBytes;
        artifact.ContainsRawAudio = payload.ContainsRawAudio;
        artifact.ContainsRawVideo = payload.ContainsRawVideo;
        // WT-473. Null when the event predates the field or LiveKit did not report it; the UI reads
        // that as "not seekable" rather than substituting zero. A start time the Started event
        // already stored is kept rather than erased by a Completed that lacks one.
        if (payload.StartedAt != null)
            artifact.RecordingStartedAt = payload.StartedAt.Value.ToUniversalTime();
    }

    /// <summary>Upper bound on <c>failure_reason</c>; LiveKit's error is free text of unknown size.</summary>
    public const int FailureReasonMaxLength = 500;

    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(
        @"[a-z][a-z0-9+.-]*://\S+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// WT-824: the stored reason — "&lt;host-facing sentence&gt; (LiveKit &lt;status&gt;: &lt;error&gt;)".
    ///
    /// LiveKit's error is kept because it is the only diagnosis there is: egress runs in LiveKit
    /// Cloud and the two production recordings failed with nothing anyone could read afterwards.
    /// It is shown to whoever may read the meeting's outputs, so URLs in it (storage endpoints,
    /// bucket URLs, presigned links) are replaced with "[url]"; the kind of failure — upload
    /// refused, access denied, start signal not received — is what survives, and is what matters.
    /// </summary>
    public static string DescribeFailure(string? reason, string? liveKitStatus, string? liveKitError)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "The recording failed." : reason.Trim();

        var status = string.IsNullOrWhiteSpace(liveKitStatus) ? null : liveKitStatus.Trim();
        var error = string.IsNullOrWhiteSpace(liveKitError)
            ? null
            : UrlPattern.Replace(liveKitError.Trim(), "[url]");

        if (status != null || error != null)
        {
            var detail = status != null && error != null ? $"{status}: {error}" : status ?? error;
            text = $"{text} (LiveKit {detail})";
        }

        return text.Length <= FailureReasonMaxLength
            ? text
            : text[..(FailureReasonMaxLength - 1)] + "…";
    }

    private static bool IsStatus(TranslationRoomArtifact artifact, string status) =>
        string.Equals(artifact.Status, status, StringComparison.OrdinalIgnoreCase);
}
