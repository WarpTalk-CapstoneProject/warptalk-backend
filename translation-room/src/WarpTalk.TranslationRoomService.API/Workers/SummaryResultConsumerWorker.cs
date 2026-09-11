using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// The other half of a summary rewrite: warptalk-ai's SummaryTemplateWorker publishes the
/// regenerated summary here, and this replaces the room's SUMMARY_EXPORT artifact content
/// with it.
///
/// This service owns that artifact — ArtifactsFinalizer writes it when a meeting ends — so
/// the update belongs here rather than in whichever service happened to take the request.
/// </summary>
public class SummaryResultConsumerWorker : BackgroundService
{
    private const string StreamName = "assistant:summary_results";
    private const string GroupName = "translation-room-summary-consumers";

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SummaryResultConsumerWorker> _logger;
    private readonly string _consumerName = $"translation-room-{Environment.MachineName}";

    public SummaryResultConsumerWorker(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<SummaryResultConsumerWorker> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();

        // GUARDED. An exception escaping ExecuteAsync trips
        // BackgroundServiceExceptionBehavior.StopHost and takes the whole service down, so a
        // Redis that is merely slow to accept connections during a parallel deploy would
        // turn into a failed release. Retry here instead.
        if (!await EnsureConsumerGroupAsync(db, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await db.StreamReadGroupAsync(
                    StreamName, GroupName, _consumerName, position: ">", count: 10);

                if (entries.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                    continue;
                }

                foreach (var entry in entries)
                {
                    try
                    {
                        await ProcessEntryAsync(entry, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to apply summary result {EntryId}", entry.Id);
                    }
                    finally
                    {
                        // Acknowledged either way. A summary that could not be applied is not
                        // worth redelivering forever — the requester can simply ask again,
                        // and a stuck entry would block every later rewrite behind it.
                        await db.StreamAcknowledgeAsync(StreamName, GroupName, entry.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Summary result consumer loop failed");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task ProcessEntryAsync(StreamEntry entry, CancellationToken ct)
    {
        var fields = entry.Values.ToDictionary(
            value => value.Name.ToString(),
            value => value.Value.ToString());

        if (!Guid.TryParse(fields.GetValueOrDefault("room_id"), out var roomId)) return;

        var requestId = fields.GetValueOrDefault("request_id", string.Empty);

        var status = fields.GetValueOrDefault("status", string.Empty);
        if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            var error = fields.GetValueOrDefault("error", "no reason given");

            // The reason still does not go anywhere near the stored summary — a rewrite that
            // failed must leave the one it failed to replace exactly as it was (WT-530). It goes
            // to the person who pressed the button instead, which is where it was addressed all
            // along. Until WT-669 this line was the end of the road: the requester watched an
            // unchanged panel for ninety seconds and was then told, in general terms, that
            // something had not arrived.
            _logger.LogWarning(
                "Summary rewrite for room {RoomId} failed: {Error}",
                roomId,
                error);
            await PublishOutcomeAsync(requestId, "failed", error);
            return;
        }

        var content = fields.GetValueOrDefault("content_json", string.Empty);
        if (string.IsNullOrWhiteSpace(content))
        {
            // "completed" with nothing in it. Dropping it is right — there is nothing to write —
            // but doing so in silence left the requester waiting on a summary that had already
            // come and gone.
            _logger.LogWarning("Summary rewrite for room {RoomId} completed with no content", roomId);
            await PublishOutcomeAsync(requestId, "failed", "The rewrite came back empty.");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // WHERE THIS ANSWER GOES, AND WHY THE REQUEST HAD TO SAY.
        //
        // `canonical` replaces the room's summary — the host deciding what the meeting's summary
        // IS. `variant` lands in the cache beside it, because a reader asking to see the meeting
        // in their own language must not change what anybody else sees. The two are
        // indistinguishable from the content alone, which is exactly why the mode travels on the
        // request; absent, it means canonical, so a message published before this field existed
        // keeps the behaviour it was published under.
        if (SummaryDelivery.OrDefault(fields.GetValueOrDefault("delivery")) == SummaryDelivery.Variant)
        {
            await ApplyVariantAsync(unitOfWork, roomId, content, ct);
            await PublishOutcomeAsync(requestId, "completed", null);
            return;
        }

        var artifacts = await unitOfWork.TranslationRoomArtifactRepository
            .GetArtifactsByRoomIdAsync(roomId, ct);

        var summary = artifacts?
            .Where(artifact => artifact.ArtifactType == ArtifactType.SUMMARY_EXPORT.ToString())
            .OrderByDescending(artifact => artifact.CreatedAt)
            .FirstOrDefault();

        if (summary == null)
        {
            // Nothing to replace. Rewriting is defined as replacing the meeting's summary, so
            // inventing one here would create an artifact the finalizer never made and whose
            // other columns nobody set.
            _logger.LogWarning("No summary artifact to rewrite for room {RoomId}", roomId);
            await PublishOutcomeAsync(
                requestId,
                "failed",
                "This meeting has no summary to rewrite. It needs to be finalized first.");
            return;
        }

        summary.Content = content;
        // Moved here and nowhere else on this path. Without it "is this summary out of date?"
        // could only ever answer yes, because regenerating would not clear the comparison — and a
        // staleness warning that cannot turn itself off stops meaning anything.
        summary.UpdatedAt = DateTime.UtcNow;
        unitOfWork.TranslationRoomArtifactRepository.Update(summary);
        await unitOfWork.SaveChangesAsync(ct);

        // The indexed copy has to follow the artifact. A rewrite that only updated the
        // artifact would leave the Knowledge page and WarpBot answering from the summary this
        // room no longer has — the chunk ids are derived from the room, so this overwrites the
        // previous points rather than adding a second version alongside them.
        await ReindexSummaryAsync(scope, roomId, content, ct);

        _logger.LogInformation(
            "Rewrote the summary for room {RoomId} using template {TemplateKey}",
            roomId,
            fields.GetValueOrDefault("template_key", "general"));

        // AFTER the save, never before. This is what the browser stops polling on, so publishing
        // it earlier would mean a client refetching the artifact a moment before the new content
        // reached it and reading the old summary as the answer.
        await PublishOutcomeAsync(requestId, "completed", null);
    }

    /// <summary>
    /// Leave one rewrite's outcome where the person who asked for it can find it.
    ///
    /// WT-669. A rewrite is queued, so its answer arrives long after the request returned 202 —
    /// and until now every way it could go wrong ended at a log line. The requester saw one
    /// thing for all of them: a panel that did not change.
    ///
    /// NEVER FATAL. This is how the outcome is REPORTED, not what the outcome IS: the summary is
    /// already saved by the time this runs on the success path, and on the failure paths there
    /// was nothing to save. A Redis that is down must not turn a rewrite that worked into an
    /// exception that rolls the consumer's loop — the client still has its own deadline, and
    /// falling back to that is a slower answer, not a wrong one.
    /// </summary>
    private async Task PublishOutcomeAsync(string requestId, string status, string? error)
    {
        // Empty when the message predates the id being carried through, or when the request came
        // from somewhere that does not track one. Nothing to file it under, and inventing a key
        // would leave a reply nobody is listening for.
        if (string.IsNullOrWhiteSpace(requestId)) return;

        try
        {
            // The DTO the reader deserializes into, not an anonymous shape that happens to look
            // like it: System.Text.Json matches property names case-SENSITIVELY by default, so
            // `new { status, error }` would round-trip into a DTO with both fields left at their
            // defaults — a "pending" for every outcome, and nothing to say why.
            var payload = JsonSerializer.Serialize(
                new SummaryRewriteStatusDto { Status = status, Error = error });
            await _redis.GetDatabase().StringSetAsync(
                TranslationRoomConstants.SummaryRewriteStatusKeyPrefix + requestId,
                payload,
                TranslationRoomConstants.SummaryRewriteStatusTtl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not publish the outcome of summary rewrite {RequestId}; the client falls back to its own deadline",
                requestId);
        }
    }

    /// <summary>
    /// Stores one reader's rendering, keyed by the (shape, language) the AI stamped into the
    /// content it just produced.
    ///
    /// The key is read from the CONTENT rather than from the request's own template_key and
    /// summary_language, deliberately: the content's `templateKey` is what
    /// `resolve_template` actually resolved to, and an unknown key falls back to General there.
    /// Keying on what was asked for would file a General summary under "standup" and serve it
    /// forever to anyone who picked Standup, which is the shape of bug that makes a picker look
    /// like it does nothing.
    ///
    /// NOT re-indexed to the knowledge base, unlike a canonical rewrite. The Knowledge page and
    /// WarpBot answer for the room, and the room has one summary; publishing every language into
    /// the same room-derived chunk ids would have each rendering overwrite the last and leave
    /// search answering in whichever language was read most recently.
    /// </summary>
    private async Task ApplyVariantAsync(
        IUnitOfWork unitOfWork, Guid roomId, string content, CancellationToken ct)
    {
        var (templateKey, language) = ReadSummaryKey(content);

        var existing = await unitOfWork.TranslationRoomSummaryVariantRepository
            .GetAsync(roomId, templateKey, language, ct);

        var now = DateTime.UtcNow;

        if (existing == null)
        {
            await unitOfWork.TranslationRoomSummaryVariantRepository.AddAsync(
                new TranslationRoomSummaryVariant
                {
                    Id = Guid.CreateVersion7(),
                    TranslationRoomId = roomId,
                    TemplateKey = templateKey,
                    Language = language,
                    Content = content,
                    // Left null rather than carrying the requester. The row is readable by
                    // everyone who may read the room's summary, so recording one person as its
                    // owner would suggest a privacy boundary this cache does not have — and the
                    // result message does not carry a user anyway.
                    CreatedBy = null,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                ct);
        }
        else
        {
            existing.Content = content;
            existing.UpdatedAt = now;
            unitOfWork.TranslationRoomSummaryVariantRepository.Update(existing);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two readers asked for the same language at once and both found nothing cached.
            // The unique index rejected the loser, which is correct — the winner's row is the
            // same content — so this is a note, not a failure to redeliver.
            _logger.LogInformation(
                ex,
                "A {TemplateKey}/{Language} rendering for room {RoomId} was already stored by a concurrent request",
                templateKey,
                language is { Length: > 0 } ? language : "as-spoken",
                roomId);
            return;
        }

        _logger.LogInformation(
            "Stored a {TemplateKey}/{Language} rendering of room {RoomId}'s summary",
            templateKey,
            language is { Length: > 0 } ? language : "as-spoken",
            roomId);
    }

    /// <summary>
    /// The (shape, language) a generated summary is in, as stamped by the AI worker. A content
    /// that will not parse is filed as general/as-spoken — the same pair an older summary
    /// without either key is, which is what it was.
    /// </summary>
    private static (string TemplateKey, string Language) ReadSummaryKey(string contentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(contentJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return ("general", string.Empty);

            var template = document.RootElement.TryGetProperty("templateKey", out var templateNode)
                && templateNode.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(templateNode.GetString())
                    ? templateNode.GetString()!.Trim().ToLowerInvariant()
                    : "general";

            var language = document.RootElement.TryGetProperty("summaryLanguage", out var languageNode)
                && languageNode.ValueKind == JsonValueKind.String
                    ? LanguageHelper.NormalizeLanguageCode(languageNode.GetString()) ?? string.Empty
                    : string.Empty;

            return (template, language);
        }
        catch (JsonException)
        {
            return ("general", string.Empty);
        }
    }

    /// <summary>
    /// Re-publishes the rewritten summary to the workspace knowledge index.
    ///
    /// Isolated in its own try/catch: the rewrite the user asked for has already been saved,
    /// and failing to re-index it must not turn a successful rewrite into a logged failure.
    /// </summary>
    private async Task ReindexSummaryAsync(
        IServiceScope scope, Guid roomId, string content, CancellationToken ct)
    {
        try
        {
            var text = MeetingSummaryKnowledgeText.Build(content);
            if (string.IsNullOrWhiteSpace(text)) return;

            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var room = await unitOfWork.TranslationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null || room.WorkspaceId == Guid.Empty) return;

            var publisher = scope.ServiceProvider.GetRequiredService<IKnowledgeFactRequestPublisher>();
            await publisher.PublishAsync(
                room.WorkspaceId,
                "meeting_summary",
                roomId,
                room.Title,
                text,
                indexSourceText: true,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not re-index the rewritten summary for room {RoomId}", roomId);
        }
    }

    private async Task<bool> EnsureConsumerGroupAsync(IDatabase db, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await db.StreamCreateConsumerGroupAsync(StreamName, GroupName, "0", createStream: true);
                return true;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "Could not create the '{Group}' consumer group on '{Stream}'; retrying in {Delay}.",
                    GroupName,
                    StreamName,
                    retryDelay);
                await Task.Delay(retryDelay, ct);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        return false;
    }
}
