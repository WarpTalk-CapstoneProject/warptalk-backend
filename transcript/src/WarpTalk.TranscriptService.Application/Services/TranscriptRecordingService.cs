using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Application.Services;

public class TranscriptRecordingService : ITranscriptRecordingService
{
    // Same Redis pub/sub channel TranslationRoomService already publishes RoomStarted/RoomEnded/
    // TranslationStopped on — TranslationRoomRedisSubscriberService (Gateway) is already
    // subscribed to it. Reusing the transport, not the event: "TranscriptPaused"/"TranscriptResumed"
    // are new command names, so a client that only knows the old commands ignores them, and
    // nothing here can be mistaken for "translation stopped" (which the AI workers key off of).
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";
    private const string TranscriptPausedCommand = "TranscriptPaused";
    private const string TranscriptResumedCommand = "TranscriptResumed";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranscriptPauseAccess _pauseAccess;
    private readonly ITranscriptReadAccess _readAccess;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<TranscriptRecordingService> _logger;

    public TranscriptRecordingService(
        IUnitOfWork unitOfWork,
        ITranscriptPauseAccess pauseAccess,
        ITranscriptReadAccess readAccess,
        ILogger<TranscriptRecordingService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _unitOfWork = unitOfWork;
        _pauseAccess = pauseAccess;
        _readAccess = readAccess;
        _logger = logger;
        _redis = redis;
    }

    public async Task<Result> PauseAsync(Guid translationRoomId, Guid callerId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _pauseAccess.IsRoomHostAsync(translationRoomId, callerId, cancellationToken))
                return Result.Failure("Only the host can pause the transcript.", "FORBIDDEN");

            var active = await _unitOfWork.TranscriptPauseWindows.GetActiveWindowByRoomIdAsync(translationRoomId, cancellationToken);
            if (active != null)
                return Result.Failure("The transcript is already paused.", "INVALID_STATE");

            var now = DateTime.UtcNow;
            await _unitOfWork.TranscriptPauseWindows.AddAsync(new TranscriptPauseWindow
            {
                TranslationRoomId = translationRoomId,
                StartedAt = now,
                PausedBy = callerId,
                CreatedAt = now,
                UpdatedAt = now,
            }, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishCommandAsync(TranscriptPausedCommand, translationRoomId, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing transcript for room {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> ResumeAsync(Guid translationRoomId, Guid callerId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _pauseAccess.IsRoomHostAsync(translationRoomId, callerId, cancellationToken))
                return Result.Failure("Only the host can resume the transcript.", "FORBIDDEN");

            var active = await _unitOfWork.TranscriptPauseWindows.GetActiveWindowByRoomIdAsync(translationRoomId, cancellationToken);
            if (active == null)
                return Result.Failure("The transcript is not paused.", "INVALID_STATE");

            active.EndedAt = DateTime.UtcNow;
            active.ResumedBy = callerId;
            active.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.TranscriptPauseWindows.Update(active);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishCommandAsync(TranscriptResumedCommand, translationRoomId, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming transcript for room {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IReadOnlyList<TranscriptPauseWindowDto>>> GetPauseWindowsAsync(Guid translationRoomId, Guid callerId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Read access, not pause access: every participant sees the divider, not only the host.
            if (!await _readAccess.CanReadRoomTranscriptAsync(translationRoomId, callerId, cancellationToken))
                return Result.Failure<IReadOnlyList<TranscriptPauseWindowDto>>("You do not have access to this transcript.", "FORBIDDEN");

            var windows = await _unitOfWork.TranscriptPauseWindows.GetWindowsByRoomIdAsync(translationRoomId, cancellationToken);
            return Result.Success<IReadOnlyList<TranscriptPauseWindowDto>>(
                windows.Select(w => w.ToDto()).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing transcript pause windows for room {RoomId}", translationRoomId);
            return Result.Failure<IReadOnlyList<TranscriptPauseWindowDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    // Best-effort, same as PublishTranslationStoppedAsync on the translation-room side: a lost
    // publish only delays the live banner to a poll-driven refresh, and must never fail the
    // pause/resume call that already committed to the database.
    private async Task PublishCommandAsync(string command, Guid roomId, CancellationToken ct)
    {
        if (_redis is null)
            return;

        try
        {
            var payload = JsonSerializer.Serialize(new { Command = command, RoomId = roomId.ToString() });
            var subscriber = _redis.GetSubscriber();
            await subscriber.PublishAsync(RedisChannel.Literal(GatewayCommandsChannel), payload);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to publish {Command} for RoomId: {RoomId}", command, roomId);
        }
    }
}
