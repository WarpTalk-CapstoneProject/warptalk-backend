using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.Shared.Events;

namespace WarpTalk.TranscriptService.Infrastructure.Redis;

/// <summary>
/// Publishes this workspace's glossary — its own terms, merged with the system-managed
/// global glossary (transcript.global_glossary_terms, see docs/global-glossary-plan.md) —
/// into the two Redis keys warptalk-ai reads per meeting:
/// <c>translationRoom:{roomId}:stt_prompt</c> (stt_worker's contextual-biasing prompt, see
/// STTWorker._get_stt_prompt) and <c>translationRoom:{roomId}:mt_glossary</c>
/// (translation_worker's exact-mapping/keep-verbatim glossary, see TranslationWorker.
/// _get_mt_glossary) — so code-switched terms (e.g. "architect" spoken inside a Vietnamese
/// sentence) have a chance of being recognized and translated consistently instead of being
/// phonetically mangled by STT and then mistranslated. See docs/code-switching-research.md.
///
/// Mirrors WorkspaceService.MeetingStartedEventConsumer's exact subscribe-to-"meeting.started"
/// pattern, but lives here (not WorkspaceService) because Glossary/GlossaryTerm are owned by
/// TranscriptService — no cross-service call needed for the workspace-level half.
/// </summary>
public class GlossaryStartedEventConsumer : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GlossaryStartedEventConsumer> _logger;

    // Bounds the STT prompt: OpenAI's own guidance is to bias with a short, representative
    // list, not a keyword dump — an overlong prompt risks the model hallucinating terms
    // into places they weren't spoken. Workspace terms (already curated by admins via the
    // Terminology UI) always get first claim on this budget; global terms only fill what's
    // left over — see MergeTerms.
    private const int MaxTermsInPrompt = 60;
    // NOTE: published contextual-biasing studies put the sweet spot near 100 entries
    // (~40% relative improvement on biased-word error rate), with degradation of the
    // UNBIASED word error rate only appearing around 1000. warptalk-ai caps at 100 too
    // (stt_worker/model.py:_normalized_keywords), so 24 — not either cap — is the binding
    // constraint today. Raising it is likely a real accuracy win, but it should be
    // measured rather than assumed, so it is left alone here and called out instead.
    /// <summary>
    /// How many terms the recogniser is biased toward. WT-426 cut this from 24.
    ///
    /// A bias list is not free vocabulary — it is a thumb on the scale, and on marginal audio the
    /// model resolves ambiguity INTO the list. Production, 15 Aug, a noisy meeting: a speaker said
    /// "voice clone" and the transcript read "Cũng là ChatGPT"; elsewhere it emitted "WarpTalk,
    /// WarpBot, Codex." as a whole utterance. Every one of those is a glossary term.
    ///
    /// So the budget is small on purpose. It should hold the handful of names this room genuinely
    /// cannot be transcribed without, not a dictionary.
    /// </summary>
    private const int MaxSttKeywords = 10;

    /// <summary>
    /// Of that budget, how much a PLATFORM-WIDE term may take.
    ///
    /// Global terms earned their place in the recogniser honestly — "Codex" came back as "cô đích"
    /// without them. But they are global: most of them have nothing to do with any given meeting,
    /// and a term nobody is going to say is pure hallucination surface. Workspace terms fill the
    /// budget first and globals take what is left.
    /// </summary>
    private const int MaxGlobalSttKeywords = 3;
    // Below this, an entry is treated as a false-accept risk rather than a useful hint;
    // see IsUsefulSttKeyword for the acronym exception.
    private const int MinSttKeywordLength = 3;
    private static readonly TimeSpan PromptTtl = TimeSpan.FromHours(24);

    public GlossaryStartedEventConsumer(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<GlossaryStartedEventConsumer> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();

        // GUARDED: an exception escaping ExecuteAsync trips the default
        // BackgroundServiceExceptionBehavior.StopHost and takes the whole TranscriptService
        // process down, not just this consumer. The app and infra roles deploy in parallel, so
        // reaching this line before Redis is accepting connections is routine. Same
        // bounded-backoff shape as HostFallbackConsumerWorker.
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(RedisChannel.Literal("meeting.started"), async (channel, message) =>
                {
                    try
                    {
                        if (TryParseStartedEvent(message.ToString(), out var payload))
                        {
                            await PublishGlossaryPromptsAsync(
                                payload!.TranslationRoomId.ToString(),
                                payload.WorkspaceId,
                                payload.Title,
                                payload.Description,
                                stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing meeting.started event for glossary STT/MT prompt.");
                    }
                });

                _logger.LogInformation("GlossaryStartedEventConsumer is listening to 'meeting.started' channel.");
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "GlossaryStartedEventConsumer could not subscribe to 'meeting.started'; retrying in {RetryDelay}. "
                    + "Glossary STT/MT prompts are not being published until it succeeds.",
                    retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }

    internal static bool TryParseStartedEvent(
        string serializedEvent,
        out MeetingStartedEventPayload? payload)
    {
        payload = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope<MeetingStartedEventPayload>>(
                serializedEvent);
            if (envelope == null ||
                envelope.EventType != MeetingEventTypes.Started ||
                envelope.SchemaVersion != DomainEventEnvelope.CurrentSchemaVersion ||
                envelope.Payload.TranslationRoomId == Guid.Empty ||
                (envelope.Payload.WorkspaceId == Guid.Empty &&
                 string.IsNullOrWhiteSpace(envelope.Payload.Title) &&
                 string.IsNullOrWhiteSpace(envelope.Payload.Description)))
            {
                return false;
            }

            payload = envelope.Payload;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Uniform shape for a term regardless of whether it came from the workspace's
    /// own glossary or the global glossary — lets MergeTerms treat both the same way. Internal
    /// (not private) so WarpTalk.TranscriptService.Tests can unit test MergeTerms/NormalizeKey
    /// directly — see InternalsVisibleTo in this project's AssemblyInfo.cs.</summary>
    internal readonly record struct PromptTerm(string Source, string Target, int Priority);

    public async Task PublishGlossaryPromptsAsync(
        string roomId,
        Guid workspaceId,
        string? title,
        string? description,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var glossaries = await unitOfWork.Glossaries.FindAsync(g => g.WorkspaceId == workspaceId && g.IsActive, ct);
        var glossaryIds = glossaries.Select(g => g.Id).ToList();

        var workspaceTerms = glossaryIds.Count == 0
            ? new List<PromptTerm>()
            : (await unitOfWork.GlossaryTerms.FindAsync(t => glossaryIds.Contains(t.GlossaryId) && t.IsActive, ct))
                .Select(t => new PromptTerm(t.SourceTerm, t.TargetTerm, t.Priority))
                .ToList();

        var globalTerms = await LoadGlobalTermsAsync(scope, workspaceId, ct);

        var (merged, droppedAsOverridden, droppedAsOverBudget) = MergeTerms(workspaceTerms, globalTerms, MaxTermsInPrompt);
        // Workspace terms first, globals only in what is left. WT-426.
        //
        // This used to pass `merged` — every term, workspace and global together, ranked only by
        // priority. That was the right shape for the problem it solved: global terms reached the
        // translator but never the recogniser, so "Codex" came back as "cô đích" and no amount of
        // curating the global glossary could fix it.
        //
        // It overshot. A bias list is a thumb on the scale, and on marginal audio the model
        // resolves ambiguity INTO it. A noisy production meeting on 15 Aug transcribed "voice
        // clone" as "Cũng là ChatGPT" and emitted "WarpTalk, WarpBot, Codex." as an utterance
        // nobody spoke. Global terms are the worst offenders precisely because they are global:
        // most have nothing to do with the meeting in the room, so they are hallucination surface
        // with no upside.
        //
        // The fix is not to take them back out — "Codex" still needs to be heard. It is to stop
        // them crowding out the terms this workspace actually uses, and to keep the whole list
        // small enough that it biases rather than dictates.
        var sttKeywords = BuildSttKeywords(
            workspaceTerms, globalTerms, MaxSttKeywords, MaxGlobalSttKeywords);

        var meetingContext = BuildMeetingContext(title, description);
        if (merged.Count == 0 && string.IsNullOrEmpty(meetingContext))
        {
            _logger.LogInformation(
                "No meeting context or glossary terms for workspace {WorkspaceId}; skipping STT/MT prompt for room {RoomId}.",
                workspaceId, roomId);
            return;
        }

        var db = _redis.GetDatabase();

        // Short meeting context shapes ambiguous/code-switched speech without asking the model
        // to invent content. Glossary terms remain a compact bias list rather than a keyword dump.
        var sttPrompt = BuildSttPrompt(title, description, merged);
        await db.StringSetAsync($"translationRoom:{roomId}:stt_prompt", sttPrompt, PromptTtl);
        if (sttKeywords.Count > 0)
        {
            await db.StringSetAsync(
                $"translationRoom:{roomId}:stt_keywords",
                JsonSerializer.Serialize(sttKeywords),
                PromptTtl);
        }
        if (!string.IsNullOrEmpty(meetingContext))
        {
            await db.StringSetAsync(
                $"translationRoom:{roomId}:meeting_context",
                meetingContext,
                PromptTtl);
        }

        // translation_worker._get_mt_glossary parses this JSON to build exact source→target
        // mappings, or "keep verbatim" instructions when source == target.
        if (merged.Count > 0)
        {
            var mtGlossary = JsonSerializer.Serialize(merged.Select(t => new { source = t.Source, target = t.Target }));
            await db.StringSetAsync($"translationRoom:{roomId}:mt_glossary", mtGlossary, PromptTtl);
        }

        _logger.LogInformation(
            "Published meeting context + {SttKeywordCount} workspace STT keywords + MT glossary for room {RoomId}: {TermCount} terms " +
            "({WorkspaceCount} workspace, {GlobalCount} global; {OverriddenCount} global terms " +
            "shadowed by a workspace override, {OverBudgetCount} global terms dropped over the " +
            "{MaxTerms}-term prompt budget).",
            sttKeywords.Count, roomId, merged.Count,
            workspaceTerms.Count, globalTerms.Count,
            droppedAsOverridden, droppedAsOverBudget, MaxTermsInPrompt);
    }

    internal static string BuildSttPrompt(
        string? title,
        string? description,
        IReadOnlyCollection<PromptTerm> terms)
    {
        var parts = new List<string>();
        var meetingContext = BuildMeetingContext(title, description);
        if (!string.IsNullOrEmpty(meetingContext))
            parts.Add(meetingContext);
        if (terms.Count > 0)
        {
            var termList = string.Join(", ", terms.Select(t => t.Source));
            parts.Add($"Terms that may appear in this meeting: {termList}.");
        }
        return string.Join(" ", parts);
    }

    internal static string BuildMeetingContext(string? title, string? description)
    {
        static string Normalize(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            var normalized = string.Join(
                " ",
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength].TrimEnd();
        }

        var normalizedTitle = Normalize(title, 120);
        var normalizedDescription = Normalize(description, 360);
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(normalizedTitle))
            parts.Add($"Meeting topic: {normalizedTitle}.");
        if (!string.IsNullOrEmpty(normalizedDescription))
            parts.Add($"Meeting context: {normalizedDescription}.");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Contextual-biasing keywords for the STT model.
    /// </summary>
    /// <param name="terms">
    /// The MERGED term list — workspace and global together, already ordered by priority.
    /// This parameter was named <c>workspaceTerms</c> and was passed only the workspace's
    /// own terms, which is how a global proper noun could be translated correctly and
    /// recognised as something else entirely.
    /// </param>
    /// <summary>
    /// Whether a term earns one of the scarce contextual-biasing slots.
    /// <para>
    /// Contextual-biasing research is consistent that very short entries are the main
    /// source of false accepts: a two-letter string matches fragments of ordinary speech
    /// constantly, so it drags unrelated audio toward the biased term and costs accuracy
    /// on everything NOT in the list. That matters doubly here because the budget is only
    /// <see cref="MaxSttKeywords"/> entries and BuildSttKeywords fills it from BOTH sides
    /// of every pair — the Vietnamese target of a term is a keyword too, so short function
    /// words can crowd out the proper nouns the list exists for.
    /// </para>
    /// <para>
    /// Acronyms are the deliberate exception. "AI", "QA" and "ML" are exactly the terms
    /// worth biasing and are short by nature, so two or more capitals buys a pass — that
    /// keeps "gRPC" and "iOS" while still rejecting "và", "là" and "of".
    /// </para>
    /// </summary>
    internal static bool IsUsefulSttKeyword(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.Length >= MinSttKeywordLength)
            return true;
        return value.Count(char.IsUpper) >= 2;
    }

    /// <summary>
    /// The terms the recogniser is biased toward, workspace-first. WT-426.
    /// </summary>
    /// <remarks>
    /// Two budgets, not one. `maxKeywords` bounds the whole list; `maxGlobalKeywords` bounds how
    /// much of it a platform-wide term may take.
    ///
    /// The split exists because the two kinds of term carry different risk. A workspace's own
    /// glossary describes what people in that workspace say. A global term describes what somebody
    /// on the platform says — usually not the people in this room — so it is mostly hallucination
    /// surface, and on noisy audio the model reaches for it. Bounding globals separately keeps the
    /// fix that put them here ("Codex" was being heard as "cô đích") without letting them fill a
    /// list they were never the point of.
    /// </remarks>
    internal static List<string> BuildSttKeywords(
        IReadOnlyCollection<PromptTerm> workspaceTerms,
        IReadOnlyCollection<PromptTerm> globalTerms,
        int maxKeywords,
        int maxGlobalKeywords)
    {
        if (maxKeywords <= 0)
            return new List<string>();

        var keywords = new List<string>(maxKeywords);
        var seen = new HashSet<string>();

        // Workspace first, and NOT interleaved by priority with the globals. A global term with a
        // high priority would otherwise take a slot from a workspace term that is more likely to
        // actually be said in this room — which is the whole distinction being drawn.
        Fill(workspaceTerms, maxKeywords);
        Fill(globalTerms, Math.Min(maxKeywords, keywords.Count + maxGlobalKeywords));

        return keywords;

        void Fill(IReadOnlyCollection<PromptTerm> terms, int ceiling)
        {
            foreach (var term in terms.OrderByDescending(t => t.Priority))
            {
                foreach (var value in new[] { term.Source, term.Target })
                {
                    if (keywords.Count >= ceiling)
                        return;

                    var cleaned = string.Join(
                        " ",
                        value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    if (string.IsNullOrEmpty(cleaned) || !IsUsefulSttKeyword(cleaned))
                        continue;
                    if (!seen.Add(NormalizeKey(cleaned)))
                        continue;

                    keywords.Add(cleaned);
                }
            }
        }
    }

    /// <summary>
    /// The system-managed global glossary (published rows of transcript.global_glossary_terms)
    /// — gated by both a process-wide kill switch (GlobalGlossary:Enabled, default true; flips
    /// off instantly without a DB change or redeploy if a bad term is discovered) and a
    /// per-workspace opt-out (AiUsagePolicy.UseGlobalGlossary via WorkspaceService's
    /// GetWorkspaceSettings gRPC call — same pattern TranscriptRedisConsumerService already
    /// uses for AllowExternalLlm). Fails OPEN on gRPC error (defaults to included) — same
    /// "opt-out, unset ⇒ allowed" convention as everywhere else this policy is resolved.
    /// </summary>
    private async Task<List<PromptTerm>> LoadGlobalTermsAsync(IServiceScope scope, Guid workspaceId, CancellationToken ct)
    {
        var killSwitchEnabled = _configuration.GetValue("GlobalGlossary:Enabled", true);
        if (!killSwitchEnabled)
        {
            return new List<PromptTerm>();
        }

        bool workspaceOptedIn;
        try
        {
            var workspaceClient = scope.ServiceProvider
                .GetRequiredService<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>();
            var settings = await workspaceClient.GetWorkspaceSettingsAsync(
                new WarpTalk.Shared.Protos.GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);
            workspaceOptedIn = settings.UseGlobalGlossary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve global glossary opt-out for workspace {WorkspaceId}; defaulting to included.", workspaceId);
            workspaceOptedIn = true;
        }

        if (!workspaceOptedIn)
        {
            return new List<PromptTerm>();
        }

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var terms = await unitOfWork.GlobalGlossaryTerms.FindAsync(
            t => t.Status == "published" && t.DeletedAt == null, ct);

        return terms
            .OrderByDescending(t => t.Priority)
            .Select(t => new PromptTerm(t.Term, t.PreferredTranslation, t.Priority))
            .ToList();
    }

    /// <summary>
    /// Merges workspace and global terms: a workspace term always wins over a global term with
    /// the same normalized key (trim + lowercase — see docs/global-glossary-plan.md §2.3), and
    /// the combined list is capped at maxTerms with workspace terms (by priority) filling the
    /// budget first, global terms (by priority) filling whatever's left. Returns the merged list
    /// plus counts of global terms dropped for each reason, purely for the log line above — a
    /// silently-truncated or silently-shadowed term reads as "the feature doesn't work" to
    /// whoever configured it.
    /// </summary>
    internal static (List<PromptTerm> Merged, int DroppedAsOverridden, int DroppedAsOverBudget) MergeTerms(
        List<PromptTerm> workspaceTerms, List<PromptTerm> globalTerms, int maxTerms)
    {
        var orderedWorkspace = workspaceTerms.OrderByDescending(t => t.Priority).ToList();
        var workspaceKeys = new HashSet<string>(orderedWorkspace.Select(t => NormalizeKey(t.Source)));

        var overriddenCount = globalTerms.Count(t => workspaceKeys.Contains(NormalizeKey(t.Source)));
        var eligibleGlobal = globalTerms
            .Where(t => !workspaceKeys.Contains(NormalizeKey(t.Source)))
            .OrderByDescending(t => t.Priority)
            .ToList();

        var merged = new List<PromptTerm>(orderedWorkspace.Take(maxTerms));
        var remainingBudget = Math.Max(0, maxTerms - merged.Count);
        merged.AddRange(eligibleGlobal.Take(remainingBudget));

        var overBudgetCount = Math.Max(0, eligibleGlobal.Count - remainingBudget)
            + Math.Max(0, orderedWorkspace.Count - maxTerms);

        return (merged, overriddenCount, overBudgetCount);
    }

    internal static string NormalizeKey(string term) =>
        string.Join(" ", term.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
