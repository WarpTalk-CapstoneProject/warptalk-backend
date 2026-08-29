using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc />
public class MicrophoneNoiseReductionService : IMicrophoneNoiseReductionService
{
    /// <summary>
    /// The contract with STTWorker._get_noise_reduction. Both halves have to agree on this string,
    /// and the AI side reads it straight out of Redis rather than through an API.
    ///
    /// LOWER-CASED, and that is not cosmetic. The AI side receives the speaker id as LiveKit
    /// reported it, whose casing does not reliably match this GUID — base_worker compares
    /// SourceUserId with .lower() on both sides for exactly that reason. Redis keys are
    /// case-sensitive, so an un-normalised id here is a write half that never meets its reader.
    /// Guid.ToString() is already lower-case; ToLowerInvariant states the requirement rather than
    /// relying on that, because the requirement is what a future edit needs to see.
    /// </summary>
    private static string KeyFor(Guid roomId, Guid userId) =>
        $"translationRoom:{roomId}:participant:{userId.ToString().ToLowerInvariant()}:noise_reduction";

    /// <summary>
    /// Exactly what the provider accepts. An unrecognised string fails the WHOLE session update on
    /// the AI side, taking the language hint and the keywords down with it — _degrade_session_config
    /// exists because that has happened before. So it is refused here, at the edge, rather than
    /// written and discovered mid-meeting.
    /// </summary>
    private static readonly HashSet<string> Modes =
        new(StringComparer.OrdinalIgnoreCase) { Off, "near_field", "far_field" };

    private const string Off = "off";

    /// <summary>
    /// Which denoiser the client says is carrying the load. Bounded for the same reason Modes is:
    /// this reaches a log field that somebody will later group by, and a free-text value makes
    /// that grouping meaningless.
    /// </summary>
    private static readonly HashSet<string> Processors =
        new(StringComparer.OrdinalIgnoreCase) { "krisp", "browser" };

    /// <summary>
    /// Enough for any real failure message the client sends, short enough that a participant
    /// cannot use a diagnostics endpoint to write arbitrary volume into the service log.
    /// </summary>
    private const int MaxReasonChars = 300;

    /// <summary>
    /// Separate from the mode key above, and NOT read by the AI side. Nothing consumes this; it
    /// exists so a live meeting can be inspected while it is happening, next to the mode key its
    /// name deliberately resembles.
    /// </summary>
    private static string ReportKeyFor(Guid roomId, Guid userId) =>
        $"translationRoom:{roomId}:participant:{userId.ToString().ToLowerInvariant()}:noise_suppression";

    /// <summary>
    /// Long enough to outlive any meeting, short enough that abandoned rooms do not accumulate
    /// keys forever. Redis here runs allkeys-lru and has evicted live meeting state before, so
    /// nothing this service writes is allowed to be immortal.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly ITranslationRoomParticipantRepository _participants;
    private readonly IRedisStateRepository _redis;
    private readonly ILogger<MicrophoneNoiseReductionService> _logger;

    public MicrophoneNoiseReductionService(
        ITranslationRoomParticipantRepository participants,
        IRedisStateRepository redis,
        ILogger<MicrophoneNoiseReductionService> logger)
    {
        _participants = participants;
        _redis = redis;
        _logger = logger;
    }

    public async Task<Result<string>> GetAsync(
        Guid roomId, Guid userId, CancellationToken ct = default)
    {
        var participant = await _participants.GetByRoomAndUserAsync(roomId, userId, ct);
        if (participant == null)
        {
            return Result.Failure<string>(
                AudioRouteConstants.ErrorParticipantNotInRoom, ErrorCodes.NotFound);
        }

        try
        {
            var raw = await _redis.StringGetAsync(KeyFor(roomId, userId));
            var mode = raw?.Trim().ToLowerInvariant();
            // Anything unrecognised reads as "off" rather than as an error: the AI side ignores a
            // value it does not recognise and falls back, so "off" is the honest description of
            // what the pipeline will actually do.
            return Result.Success(mode != null && Modes.Contains(mode) ? mode : Off);
        }
        catch (Exception ex)
        {
            // Never an error to the caller. The AI side falls back when it cannot read this key,
            // so "off" is again what the audio will actually do.
            _logger.LogWarning(
                ex, "Could not read microphone denoising for {UserId} in room {RoomId}.",
                userId, roomId);
            return Result.Success(Off);
        }
    }

    public async Task<Result<string>> SetAsync(
        Guid roomId, Guid userId, string mode, CancellationToken ct = default)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (!Modes.Contains(normalized))
        {
            return Result.Failure<string>(
                "Noise reduction mode must be one of: off, near_field, far_field.",
                ErrorCodes.ValidationError);
        }

        // Membership, NOT hosting. See IMicrophoneNoiseReductionService for why gating this on the
        // host would be a bug: it only changes how this one caller's own microphone is handled.
        var participant = await _participants.GetByRoomAndUserAsync(roomId, userId, ct);
        if (participant == null)
        {
            return Result.Failure<string>(
                AudioRouteConstants.ErrorParticipantNotInRoom, ErrorCodes.NotFound);
        }

        try
        {
            await _redis.StringSetAsync(KeyFor(roomId, userId), normalized, Ttl);
        }
        catch (Exception ex)
        {
            // Reported rather than swallowed. The person just changed a setting and is about to
            // listen for the difference; telling them it worked when the write failed makes the
            // feature look broken instead of the write.
            _logger.LogError(
                ex, "Could not write microphone denoising for {UserId} in room {RoomId}.",
                userId, roomId);
            return Result.Failure<string>(
                "Could not change noise reduction right now.", ErrorCodes.InternalServerError);
        }

        // At information level on purpose: this changes how one participant is transcribed, and it
        // is among the first things worth knowing when their transcript quality is questioned
        // afterwards.
        _logger.LogInformation(
            "Microphone denoising set to {Mode} for {UserId} in room {RoomId}.",
            normalized, userId, roomId);

        // No route republish. The STT worker re-reads this key on a short TTL and issues a
        // session.update on the live socket when it differs, so the change reaches the pipeline on
        // its own within seconds — and the route payload is not where this lives.
        return Result.Success(normalized);
    }

    public async Task<Result<bool>> ReportClientSuppressionAsync(
        Guid roomId, Guid userId, ReportNoiseSuppressionDto report, CancellationToken ct = default)
    {
        if (report == null)
        {
            return Result.Failure<bool>("A report is required.", ErrorCodes.ValidationError);
        }

        var processor = (report.Processor ?? string.Empty).Trim().ToLowerInvariant();
        if (!Processors.Contains(processor))
        {
            return Result.Failure<bool>(
                "Processor must be one of: krisp, browser.", ErrorCodes.ValidationError);
        }

        // Membership, for the same reason SetAsync checks it: this says something about a meeting,
        // and an endpoint that writes a log line for any room id on request is a log nobody can
        // trust afterwards.
        var participant = await _participants.GetByRoomAndUserAsync(roomId, userId, ct);
        if (participant == null)
        {
            return Result.Failure<bool>(
                AudioRouteConstants.ErrorParticipantNotInRoom, ErrorCodes.NotFound);
        }

        // BOUNDED BEFORE IT IS LOGGED. This is free text from a browser, and it is the one field
        // here that an attacker controls the length of. Structured logging passes it as a
        // parameter rather than a format string, so the risk is volume rather than injection —
        // but an unbounded string repeated once per participant per track change is still how a
        // log sink fills up.
        var reason = report.Reason?.Trim();
        if (!string.IsNullOrEmpty(reason) && reason.Length > MaxReasonChars)
        {
            reason = reason[..MaxReasonChars];
        }

        if (report.Enabled)
        {
            // Information, and worth having even though it is the good case: "it worked for
            // everyone except this one person" is a different problem from "it has never worked",
            // and only the successes can tell those two apart.
            _logger.LogInformation(
                "Client noise suppression ACTIVE via {Processor} for {UserId} in room {RoomId}.",
                processor, userId, roomId);
        }
        else
        {
            // Warning, not Error. The microphone is fine — the web client restores the browser's
            // own suppression before it reports, deliberately — so this is a downgrade, and it
            // should read like one. It is still the line somebody should be able to alert on:
            // every participant reporting it means the LiveKit project is not entitled at all,
            // which no amount of reloading will fix.
            _logger.LogWarning(
                "Client noise suppression DEGRADED to {Processor} for {UserId} in room {RoomId}: {Reason}",
                processor, userId, roomId, reason ?? "no reason given");
        }

        try
        {
            await _redis.StringSetAsync(
                ReportKeyFor(roomId, userId),
                report.Enabled ? processor : $"degraded:{processor}",
                Ttl);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose, and this is the one place in this service where that is right.
            // The log line above has already been written, which is the whole point of the
            // endpoint; the Redis copy only exists so a live meeting can be inspected with
            // redis-cli. Failing the request would report a diagnostics problem to a participant
            // as though their microphone were broken.
            _logger.LogWarning(
                ex, "Could not record the noise suppression report for {UserId} in room {RoomId}.",
                userId, roomId);
        }

        return Result.Success(report.Enabled);
    }
}
