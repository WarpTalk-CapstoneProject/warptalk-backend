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
        _logger.LogInformation("GlossaryStartedEventConsumer is listening to 'meeting.started' channel.");

        await subscriber.SubscribeAsync(RedisChannel.Literal("meeting.started"), async (channel, message) =>
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(message.ToString());
                if (payload.TryGetProperty("TranslationRoomId", out var roomIdElement) &&
                    payload.TryGetProperty("WorkspaceId", out var workspaceIdElement))
                {
                    var roomId = roomIdElement.GetString();
                    var workspaceIdRaw = workspaceIdElement.GetString();

                    if (!string.IsNullOrEmpty(roomId) && Guid.TryParse(workspaceIdRaw, out var workspaceId))
                    {
                        await PublishGlossaryPromptsAsync(roomId, workspaceId, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing meeting.started event for glossary STT/MT prompt.");
            }
        });
    }

    /// <summary>Uniform shape for a term regardless of whether it came from the workspace's
    /// own glossary or the global glossary — lets MergeTerms treat both the same way. Internal
    /// (not private) so WarpTalk.TranscriptService.Tests can unit test MergeTerms/NormalizeKey
    /// directly — see InternalsVisibleTo in this project's AssemblyInfo.cs.</summary>
    internal readonly record struct PromptTerm(string Source, string Target, int Priority);

    public async Task PublishGlossaryPromptsAsync(string roomId, Guid workspaceId, CancellationToken ct)
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

        if (merged.Count == 0)
        {
            _logger.LogInformation(
                "No workspace or global glossary terms for workspace {WorkspaceId}; skipping STT/MT prompt for room {RoomId}.",
                workspaceId, roomId);
            return;
        }

        var db = _redis.GetDatabase();

        // stt_worker._get_stt_prompt reads this as free text appended to its own generic
        // anti-hallucination base — a natural sentence listing the terms, not a bare list,
        // per OpenAI's transcription.prompt guidance.
        var termList = string.Join(", ", merged.Select(t => t.Source));
        var sttPrompt = $"Terms that may appear in this meeting: {termList}.";
        await db.StringSetAsync($"translationRoom:{roomId}:stt_prompt", sttPrompt, PromptTtl);

        // translation_worker._get_mt_glossary parses this JSON to build exact source→target
        // mappings, or "keep verbatim" instructions when source == target.
        var mtGlossary = JsonSerializer.Serialize(merged.Select(t => new { source = t.Source, target = t.Target }));
        await db.StringSetAsync($"translationRoom:{roomId}:mt_glossary", mtGlossary, PromptTtl);

        _logger.LogInformation(
            "Published STT prompt + MT glossary for room {RoomId}: {TermCount} terms " +
            "({WorkspaceCount} workspace, {GlobalCount} global; {OverriddenCount} global terms " +
            "shadowed by a workspace override, {OverBudgetCount} global terms dropped over the " +
            "{MaxTerms}-term prompt budget).",
            roomId, merged.Count,
            workspaceTerms.Count, globalTerms.Count,
            droppedAsOverridden, droppedAsOverBudget, MaxTermsInPrompt);
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

        var merged = new List<PromptTerm>(orderedWorkspace);
        var remainingBudget = Math.Max(0, maxTerms - merged.Count);
        merged.AddRange(eligibleGlobal.Take(remainingBudget));

        var overBudgetCount = Math.Max(0, eligibleGlobal.Count - remainingBudget);

        // A single over-budget workspace glossary already trims workspace terms themselves
        // (existing behavior) — merged can still exceed maxTerms only if workspaceTerms.Count
        // itself already did, which this method doesn't attempt to fix (that's the caller's
        // own glossary size, not this merge's job).
        return (merged, overriddenCount, overBudgetCount);
    }

    internal static string NormalizeKey(string term) =>
        string.Join(" ", term.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
