using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Infrastructure.Redis;

public class TranscriptRedisConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TranscriptRedisConsumerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private const string ConsumerGroup = "transcript-persistence";
    private readonly string _consumerName = $"transcript-{Environment.MachineName}-{Guid.NewGuid():N}";

    // Cache the resolved AllowExternalLlm flag per workspace so PublishEmbeddingIndexRequestAsync
    // (called once per persisted segment — i.e. potentially many times a second across a busy
    // meeting) doesn't fire a gRPC call to WorkspaceService on every single segment. Mirrors the
    // same "don't hit a sibling service on every chunk" reasoning as stt_worker's per-meeting
    // glossary prompt cache (warptalk-ai/stt_worker/worker.py).
    private readonly Dictionary<Guid, (bool AllowExternalLlm, DateTime CachedAt)> _workspacePolicyCache = new();
    private static readonly TimeSpan WorkspacePolicyCacheDuration = TimeSpan.FromMinutes(5);

    public TranscriptRedisConsumerService(
        IConnectionMultiplexer redis,
        ILogger<TranscriptRedisConsumerService> logger,
        IServiceProvider serviceProvider)
    {
        _redis = redis;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TranscriptRedisConsumerService started.");
        var db = _redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var streamKeys = TranscriptConsumerPollingPolicy.InputStreams;

                // Ensure consumer group exists for all streams
                foreach (var stream in streamKeys)
                {
                    try
                    {
                        await db.StreamCreateConsumerGroupAsync(stream, ConsumerGroup, "0-0", true);
                    }
                    catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
                    {
                        // Ignore
                    }
                }

                var messagesRead = 0;
                foreach (var stream in streamKeys)
                {
                    var messages = await db.StreamReadGroupAsync(stream, ConsumerGroup, _consumerName, count: 10);
                    
                    if (messages.Length > 0)
                    {
                        messagesRead += messages.Length;
                        foreach (var message in messages)
                        {
                            bool success;
                            switch (TranscriptConsumerPollingPolicy.Classify(stream))
                            {
                                case TranscriptResultStreamKind.Stt:
                                    success = await ProcessSttMessageAsync(stream, message, stoppingToken);
                                    break;
                                case TranscriptResultStreamKind.Translation:
                                    success = await ProcessTranslateMessageAsync(stream, message, stoppingToken);
                                    break;
                                case TranscriptResultStreamKind.Tts:
                                    success = await ProcessTtsMessageAsync(stream, message, stoppingToken);
                                    break;
                                default:
                                    success = true;
                                    break;
                            }
                            
                            if (success)
                            {
                                await db.StreamAcknowledgeAsync(stream, ConsumerGroup, message.Id);
                            }
                        }
                    }
                }

                var idleDelay = TranscriptConsumerPollingPolicy.DelayAfterPass(messagesRead);
                if (idleDelay > TimeSpan.Zero)
                {
                    await Task.Delay(idleDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming STT streams");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessSttMessageAsync(string streamKey, StreamEntry message, CancellationToken cancellationToken)
    {
        var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

        // The room id comes from the message payload, NOT the stream key — see
        // TranscriptConsumerPollingPolicy.TryResolveRoomId for why parsing it out of the key
        // silently discarded every message once this consumer moved to the global streams.
        if (!TranscriptConsumerPollingPolicy.TryResolveRoomId(streamKey, values, out var roomId))
        {
            _logger.LogWarning(
                "Could not resolve a room id for STT message {MessageId} on stream {Stream}",
                message.Id,
                streamKey);
            return true; // Malformed room ID, discard
        }
        
        if (!Guid.TryParse(values.GetValueOrDefault("segment_id"), out var segmentId) ||
            !Guid.TryParse(values.GetValueOrDefault("speaker_id"), out var speakerId))
        {
            _logger.LogWarning("Invalid segment data in message {MessageId}", message.Id);
            return true; // Discard invalid message
        }

        var text = values.GetValueOrDefault("text", "");
        var language = values.GetValueOrDefault("language", "unknown");
        var confidence = float.TryParse(values.GetValueOrDefault("confidence"), out var conf) ? conf : 1.0f;
        var startMs = int.TryParse(values.GetValueOrDefault("start_ms"), out var sMs) ? sMs : 0;
        var endMs = int.TryParse(values.GetValueOrDefault("end_ms"), out var eMs) ? eMs : 0;
        // shared/schemas.py STTResultMessage.to_redis() serializes this as "1"/"0", default false.
        var isFinal = values.GetValueOrDefault("is_final_chunk") == "1";

        // stt_worker publishes early per-sentence segments as they're ready, then ONE trailing
        // empty marker (text="", is_final_chunk=true) once the whole audio chunk finishes — it
        // carries no transcript content, just a "this chunk is done" signal. Without this guard
        // every audio chunk in a meeting permanently inserts a blank TranscriptSegment row (no
        // existing dedup catches it: segment_id is a fresh UUID per publish, so the "idempotent"
        // GetByIdAsync check below never matches), silently accumulating stray empty rows over a
        // long conversation. Mirrors the same guard ProcessTranslateMessageAsync already has.
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var roomClient = scope.ServiceProvider.GetRequiredService<WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>();
            var authClient = scope.ServiceProvider.GetRequiredService<WarpTalk.Shared.Protos.UserService.UserServiceClient>();
            var workspaceClient = scope.ServiceProvider.GetRequiredService<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>();

            // 1. Get or create the CURRENT transcript (head-pointer) for this room.
            var transcript = await unitOfWork.Transcripts.FirstOrDefaultAsync(
                t => t.TranslationRoomId == roomId && t.IsCurrent, cancellationToken);

            if (transcript == null)
            {
                // Fetch room details
                var roomResponse = await roomClient.GetTranslationRoomByIdAsync(
                    new WarpTalk.Shared.Protos.GetTranslationRoomRequest { Id = roomId.ToString() },
                    cancellationToken: cancellationToken);

                // A room can already have past (non-current) transcripts from an earlier
                // recording session — link the new head to the most recent one.
                var previous = (await unitOfWork.Transcripts.FindAsync(
                        t => t.TranslationRoomId == roomId, cancellationToken: cancellationToken))
                    .OrderByDescending(t => t.CreatedAt)
                    .FirstOrDefault();
                if (previous != null && previous.IsCurrent)
                {
                    previous.IsCurrent = false;
                    unitOfWork.Transcripts.Update(previous);
                }

                // Create new transcript
                transcript = new Transcript
                {
                    Id = Guid.NewGuid(),
                    TranslationRoomId = roomId,
                    WorkspaceId = Guid.TryParse(roomResponse.WorkspaceId, out var wid) ? wid : Guid.Empty,
                    SourceLanguage = language,
                    IsActive = true,
                    IsCurrent = true,
                    PreviousTranscriptId = previous?.Id,
                    TotalDurationMs = 0,
                    TotalSegments = 0,
                    LastSequenceOrder = 0
                };
                await unitOfWork.Transcripts.AddAsync(transcript, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            // 2. Persist Segment Idempotently
            var existingSegment = await unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (existingSegment == null)
            {
                // Atomic UPDATE ... RETURNING — see IUnitOfWork.AdvanceTranscriptForNewSegmentAsync's
                // doc comment for why this can't be "read transcript.LastSequenceOrder, +1, save",
                // and why total_segments/total_duration_ms are folded into the same statement
                // instead of being set on the tracked `transcript` object below.
                var sequenceOrder = await unitOfWork.AdvanceTranscriptForNewSegmentAsync(transcript.Id, endMs, cancellationToken);

                string speakerName = speakerId.ToString();
                try
                {
                    var userResponse = await authClient.GetUserByIdAsync(
                        new WarpTalk.Shared.Protos.GetUserRequest { Id = speakerId.ToString() },
                        cancellationToken: cancellationToken);
                    speakerName = userResponse.FullName;
                }
                catch (Exception) { /* Ignored for performance, should be cached realistically */ }

                var segment = new TranscriptSegment
                {
                    Id = segmentId,
                    TranscriptId = transcript.Id,
                    SpeakerParticipantId = speakerId,
                    SpeakerName = speakerName,
                    OriginalText = text,
                    OriginalLanguage = language,
                    Confidence = (decimal)confidence,
                    StartTimeMs = startMs,
                    EndTimeMs = endMs,
                    SequenceOrder = sequenceOrder,
                    IsFinal = isFinal
                };

                await unitOfWork.TranscriptSegments.AddAsync(segment, cancellationToken);

                // total_segments/total_duration_ms were already advanced atomically inside
                // AdvanceTranscriptForNewSegmentAsync above — do not also call
                // unitOfWork.Transcripts.Update(transcript) here (see that method's doc comment
                // for why that would silently revert the counter this call just advanced).
                await unitOfWork.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Persisted segment {SegmentId} (final={IsFinal}) for room {RoomId}", segmentId, isFinal, roomId);

                // Wire into the RAG pipeline incrementally, per segment, as it's transcribed —
                // there is no real "transcript finalized" event in this codebase to wait for
                // instead (transcriptService.finalize() has no backing endpoint). A publish
                // failure here must not fail segment persistence, so it's isolated in its own
                // try/catch rather than returning false (which would redeliver the whole message).
                try
                {
                    var allowExternalLlm = await ResolveAllowExternalLlmAsync(transcript.WorkspaceId, workspaceClient, cancellationToken);
                    await PublishEmbeddingIndexRequestAsync(transcript, segment, allowExternalLlm, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish embedding index request for segment {SegmentId}", segmentId);
                }

                // Billing for STT usage is metered by billing_worker (warptalk-ai/billing_worker),
                // which consumes this same stt:results event independently and writes to
                // subscription.usage_records/credit_transactions. A second charge from here would
                // double-bill the workspace for one segment — see migration
                // 019-16-07-2026-billing-schema-mismatch-and-idempotency.sql for the full story.
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting segment {SegmentId} to database", segmentId);
            return false;
        }
    }

    private async Task<bool> ProcessTranslateMessageAsync(string streamKey, StreamEntry message, CancellationToken cancellationToken)
    {
        // No room-id gate here: everything below is keyed off segment_id, and the room id was
        // never actually used. The old `streamKey.Replace("translate:results:", "")` parse could
        // not succeed on the global stream, so it just ACKed and dropped every translation — see
        // TranscriptConsumerPollingPolicy.TryResolveRoomId.
        var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

        // shared/schemas.py TranslationResultMessage.to_redis() field names — the previous
        // version of this method read "translation_id"/"text"/"target_language"/"model", none
        // of which exist on the wire (the real fields are segment_id/translated_text/target_lang/
        // translator_model). That made the Guid.TryParse below always fail, so every translation
        // message was silently discarded and transcript_translations was never actually written.
        //
        // segment_id here is NOT a plain GUID: translation_worker splits one STT segment's text
        // into per-sentence chunks and mints `f"{stt_result.segment_id}-c{idx}"` for each
        // (translation_worker/worker.py:86) so the frontend can track them as distinct caption
        // chunks. ExtractUnderlyingSegmentId strips that "-c{idx}" suffix back off to recover the
        // real TranscriptSegment.Id — without this, every translation is silently discarded here
        // too (Guid.TryParse on the raw composite string always fails).
        if (!ExtractUnderlyingSegmentId(values.GetValueOrDefault("segment_id"), out var segmentId))
        {
            _logger.LogWarning("Invalid translation data in message {MessageId}", message.Id);
            return true;
        }

        var translatedText = values.GetValueOrDefault("translated_text", "");
        var targetLang = values.GetValueOrDefault("target_lang", "unknown");
        var translatorModel = values.GetValueOrDefault("translator_model", "unknown");
        var confidence = float.TryParse(values.GetValueOrDefault("confidence"), out var conf) ? conf : 1.0f;

        if (string.IsNullOrWhiteSpace(translatedText))
        {
            return true; // flush/empty messages carry no translation to persist
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // 1. Verify Segment Exists
            var segment = await unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (segment == null)
            {
                _logger.LogWarning("Segment {SegmentId} not found for translation", segmentId);
                return false; // Retry later — the STT segment message may not have landed yet
            }

            var transcript = await unitOfWork.Transcripts.GetByIdAsync(segment.TranscriptId, cancellationToken);
            if (transcript == null)
            {
                _logger.LogWarning("Transcript {TranscriptId} not found for segment {SegmentId}", segment.TranscriptId, segmentId);
                return false;
            }

            // 2. Find-or-create the deduplicated TranslationContent for (workspace, text_hash, target_language).
            // text_hash = md5(translated_text), matching migration 017's own backfill query — two
            // segments (even from different speakers) that produce the exact same translated string
            // in the same workspace/language share one row.
            var textHash = Md5Hex(translatedText);
            var content = (await unitOfWork.TranslationContents.FindAsync(
                    tc => tc.WorkspaceId == transcript.WorkspaceId && tc.TextHash == textHash && tc.TargetLanguage == targetLang,
                    cancellationToken))
                .FirstOrDefault();

            if (content == null)
            {
                content = new TranslationContent
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = transcript.WorkspaceId,
                    TextHash = textHash,
                    TargetLanguage = targetLang,
                    TranslatedText = translatedText,
                    TranslatorModel = translatorModel,
                    Confidence = (decimal)confidence,
                    IsRetranslated = false,
                    Status = "done"
                };
                await unitOfWork.TranslationContents.AddAsync(content, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            // 3. Link the segment to this content — idempotent on the (segment_id, translation_content_id)
            // composite PK, since a Redis Streams redelivery would otherwise try to insert the same
            // pair twice.
            var alreadyLinked = (await unitOfWork.SegmentTranslationLinks.FindAsync(
                    l => l.SegmentId == segmentId && l.TranslationContentId == content.Id,
                    cancellationToken))
                .Any();

            if (!alreadyLinked)
            {
                // Supersede any current link for this (segment, language) pair — re-translation
                // (e.g. after a correction) must flip the old head rather than leave two "current" rows.
                var oldCurrentLinks = await unitOfWork.SegmentTranslationLinks.FindAsync(
                    l => l.SegmentId == segmentId && l.TargetLanguage == targetLang && l.IsCurrent,
                    cancellationToken);
                foreach (var old in oldCurrentLinks)
                {
                    old.IsCurrent = false;
                    unitOfWork.SegmentTranslationLinks.Update(old);
                }

                await unitOfWork.SegmentTranslationLinks.AddAsync(new SegmentTranslationLink
                {
                    SegmentId = segmentId,
                    TranslationContentId = content.Id,
                    TargetLanguage = targetLang,
                    IsCurrent = true,
                    DeliveredAt = DateTime.UtcNow
                }, cancellationToken);

                await unitOfWork.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Linked segment {SegmentId} to translation content {ContentId} ({TargetLang})", segmentId, content.Id, targetLang);

                // Billing for translation usage is metered by billing_worker (same reasoning as
                // the STT path above) — not duplicated here.
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting translation for segment {SegmentId} to database", segmentId);
            return false;
        }
    }

    private async Task<bool> ProcessTtsMessageAsync(string streamKey, StreamEntry message, CancellationToken cancellationToken)
    {
        // No room-id gate here — same reasoning as ProcessTranslateMessageAsync above: the parsed
        // room id was discarded into `out _` anyway, while the parse failure ACKed and dropped
        // every TTS result. See TranscriptConsumerPollingPolicy.TryResolveRoomId.
        var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

        // shared/schemas.py TTSResultMessage.to_redis() field names. segment_id is the same
        // composite "{realSegmentId}-c{idx}" string as translate:results (tts_worker consumes
        // translate:results and carries the field through unchanged) — see the matching comment
        // in ProcessTranslateMessageAsync above.
        if (!ExtractUnderlyingSegmentId(values.GetValueOrDefault("segment_id"), out var segmentId))
        {
            _logger.LogWarning("Invalid TTS data in message {MessageId}", message.Id);
            return true;
        }

        var targetLang = values.GetValueOrDefault("target_lang", "");
        var voiceType = values.GetValueOrDefault("voice_type", "default");
        var providerVoiceId = values.GetValueOrDefault("provider_voice_id", "");
        var cloneProvider = values.GetValueOrDefault("clone_provider", "");
        var anchorProvider = values.GetValueOrDefault("anchor_provider", "");
        var durationMs = int.TryParse(values.GetValueOrDefault("duration_ms"), out var dMs) ? dMs : (int?)null;

        if (string.IsNullOrWhiteSpace(targetLang) || string.IsNullOrWhiteSpace(providerVoiceId))
        {
            // Nothing to dedup/link against — e.g. a synthesis-failure fallback message.
            return true;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // The link must already exist — ProcessTranslateMessageAsync (same consumer, different
            // stream) is what creates it. If TTS somehow raced ahead of translation persistence,
            // retry later rather than writing an audio_dubbings row with no real translation_content_id.
            var currentLink = (await unitOfWork.SegmentTranslationLinks.FindAsync(
                    l => l.SegmentId == segmentId && l.TargetLanguage == targetLang && l.IsCurrent,
                    cancellationToken))
                .FirstOrDefault();

            if (currentLink == null)
            {
                _logger.LogWarning("No current translation link for segment {SegmentId}/{TargetLang} — deferring audio_dubbings write", segmentId, targetLang);
                return false; // Retry later
            }

            var content = await unitOfWork.TranslationContents.GetByIdAsync(currentLink.TranslationContentId, cancellationToken);
            if (content == null)
            {
                _logger.LogWarning("TranslationContent {ContentId} not found for segment {SegmentId}", currentLink.TranslationContentId, segmentId);
                return false;
            }

            var provider = voiceType.Equals("cloned", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cloneProvider)
                ? cloneProvider
                : !string.IsNullOrWhiteSpace(anchorProvider) ? anchorProvider : "cartesia";

            // Find-or-create on the real dedup key (workspace_id, text_hash, provider_voice_id) —
            // audio_dubbings_dedup_idx. text_hash reuses TranslationContent's own hash: it is the
            // hash of the exact text that was synthesized, no need to recompute it.
            var existingDubbing = (await unitOfWork.AudioDubbings.FindAsync(
                    ad => ad.WorkspaceId == content.WorkspaceId && ad.TextHash == content.TextHash && ad.ProviderVoiceId == providerVoiceId,
                    cancellationToken))
                .FirstOrDefault();

            if (existingDubbing == null)
            {
                await unitOfWork.AudioDubbings.AddAsync(new AudioDubbing
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = content.WorkspaceId,
                    TranslationContentId = content.Id,
                    TextHash = content.TextHash,
                    VoiceType = voiceType,
                    Provider = provider,
                    ProviderVoiceId = providerVoiceId,
                    DurationMs = durationMs,
                    Status = "done"
                }, cancellationToken);

                await unitOfWork.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Persisted audio_dubbing for translation content {ContentId} (voice={VoiceType})", content.Id, voiceType);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting audio_dubbing for segment {SegmentId} to database", segmentId);
            return false;
        }
    }

    /// <summary>
    /// Resolves this workspace's real AiUsagePolicy.AllowExternalLlm via WorkspaceService's
    /// GetWorkspaceSettings gRPC call, cached per workspace for
    /// <see cref="WorkspacePolicyCacheDuration"/> so a busy meeting doesn't fire one gRPC call
    /// per segment. Fails OPEN (returns true) on any RPC error — same "opt-out, unset ⇒ allowed"
    /// default WorkspaceGrpcService itself applies when no policy is configured, so a transient
    /// WorkspaceService outage degrades to today's behavior rather than blocking every
    /// transcript segment from being embedded.
    /// </summary>
    private async Task<bool> ResolveAllowExternalLlmAsync(
        Guid workspaceId, WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient workspaceClient, CancellationToken ct)
    {
        if (_workspacePolicyCache.TryGetValue(workspaceId, out var cached) &&
            DateTime.UtcNow - cached.CachedAt < WorkspacePolicyCacheDuration)
        {
            return cached.AllowExternalLlm;
        }

        bool allowExternalLlm;
        try
        {
            var response = await workspaceClient.GetWorkspaceSettingsAsync(
                new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);
            allowExternalLlm = response.AllowExternalLlm;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve AI usage policy for workspace {WorkspaceId}; defaulting to allowed.", workspaceId);
            allowExternalLlm = true;
        }

        _workspacePolicyCache[workspaceId] = (allowExternalLlm, DateTime.UtcNow);
        return allowExternalLlm;
    }

    /// <summary>
    /// Publishes one transcribed segment as a single chunk to the "embedding:index_requests"
    /// Redis Stream that warptalk-ai's EmbeddingWorker consumes. Field names must match
    /// EmbeddingIndexRequest.from_redis() in warptalk-ai/embedding_worker/schemas.py exactly;
    /// chunk keys (id/text/metadata) must match EmbeddingChunk. collection_id follows the
    /// "workspace_{id}" convention chat_tools.py's semantic_search already assumes.
    /// </summary>
    private async Task PublishEmbeddingIndexRequestAsync(
        Transcript transcript, TranscriptSegment segment, bool allowExternalLlm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(segment.OriginalText)) return;

        var chunk = new
        {
            id = segment.Id.ToString(),
            text = $"[{segment.SpeakerName}] {segment.OriginalText}".Trim(),
            metadata = new
            {
                transcript_id = transcript.Id.ToString(),
                translation_room_id = transcript.TranslationRoomId.ToString(),
                segment_id = segment.Id.ToString(),
                speaker_name = segment.SpeakerName,
                start_ms = segment.StartTimeMs,
            },
        };

        var entries = new NameValueEntry[]
        {
            new("job_id", Guid.NewGuid().ToString()),
            new("workspace_id", transcript.WorkspaceId.ToString()),
            new("collection_id", $"workspace_{transcript.WorkspaceId}"),
            new("source_type", "transcript"),
            new("source_id", transcript.Id.ToString()),
            new("chunks_json", JsonSerializer.Serialize(new[] { chunk })),
            new("external_llm_allowed", allowExternalLlm ? "true" : "false"),
            // No per-segment PII/DLP content scan exists for transcripts (unlike
            // WorkspaceDocument.AiEligible, which IS derived from such a scan) — that's a
            // separate, larger feature (see docs/workspace-memory-research.md §2.3/§3.4), not
            // part of this fix. Left true to preserve today's working semantic-search behavior.
            new("ai_retrieval_allowed", "true"),
            new("retention_state", transcript.IsActive ? "active" : "inactive"),
            new("deletion_state", transcript.DeletedAt == null ? "active" : "deleted"),
            new("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
        };

        var db = _redis.GetDatabase();
        await db.StreamAddAsync("embedding:index_requests", entries, maxLength: 10000, useApproximateMaxLength: true);
    }

    /// <summary>
    /// translation_worker mints segment_id as f"{stt_segment_guid}-c{idx}" (one per translated
    /// sentence chunk); tts_worker carries that same composite string through unchanged. Both
    /// consumers here need the real TranscriptSegment.Id, so parse just the GUID prefix — a raw
    /// Guid.TryParse on the composite string always fails since it isn't a valid GUID.
    /// </summary>
    private static bool ExtractUnderlyingSegmentId(string? rawSegmentId, out Guid segmentId)
    {
        segmentId = Guid.Empty;
        if (string.IsNullOrEmpty(rawSegmentId))
        {
            return false;
        }

        var guidPart = rawSegmentId.Length > 36 && rawSegmentId[36] == '-'
            ? rawSegmentId[..36]
            : rawSegmentId;

        return Guid.TryParse(guidPart, out segmentId);
    }

    private static string Md5Hex(string text)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }
}
