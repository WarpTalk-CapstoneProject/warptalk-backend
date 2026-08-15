using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using System.Text.Json;
using WarpTalk.Shared.Events;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

using PromptTerm = GlossaryStartedEventConsumer.PromptTerm;

public class MergeTermsTests
{
    [Fact]
    public void TryParseStartedEvent_AcceptsVersionedEnvelope()
    {
        var roomId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.Started,
            "meeting-service",
            workspaceId.ToString(),
            new MeetingStartedEventPayload(
                roomId,
                workspaceId,
                "Sprint planning",
                "Review the WarpTalk realtime transcript pipeline."));

        var parsed = GlossaryStartedEventConsumer.TryParseStartedEvent(
            JsonSerializer.Serialize(envelope),
            out var payload);

        Assert.True(parsed);
        Assert.Equal(roomId, payload!.TranslationRoomId);
        Assert.Equal(workspaceId, payload.WorkspaceId);
        Assert.Equal("Sprint planning", payload.Title);
        Assert.Equal("Review the WarpTalk realtime transcript pipeline.", payload.Description);
    }

    [Fact]
    public void TryParseStartedEvent_AcceptsContextOnlyRoomWithoutWorkspaceProjection()
    {
        var roomId = Guid.NewGuid();
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.Started,
            "meeting-service",
            workspaceId: null,
            new MeetingStartedEventPayload(
                roomId,
                Guid.Empty,
                "WarpTalk transcript review",
                "Discuss Docker, Kubernetes, Redis, and LiveKit."));

        var parsed = GlossaryStartedEventConsumer.TryParseStartedEvent(
            JsonSerializer.Serialize(envelope),
            out var payload);

        Assert.True(parsed);
        Assert.Equal(Guid.Empty, payload!.WorkspaceId);
        Assert.Equal("WarpTalk transcript review", payload.Title);
    }

    [Fact]
    public void BuildSttPrompt_IncludesMeetingContext_WhenGlossaryIsEmpty()
    {
        var prompt = GlossaryStartedEventConsumer.BuildSttPrompt(
            "Sprint planning",
            "Review the WarpTalk realtime transcript pipeline.",
            new List<PromptTerm>());

        Assert.Contains("Meeting topic: Sprint planning.", prompt);
        Assert.Contains("Meeting context: Review the WarpTalk realtime transcript pipeline.", prompt);
        Assert.DoesNotContain("Terms that may appear", prompt);
    }

    [Fact]
    public void BuildMeetingContext_IsBoundedAndNormalizesWhitespace()
    {
        var context = GlossaryStartedEventConsumer.BuildMeetingContext(
            "  Sprint   planning  ",
            new string('x', 800));

        Assert.StartsWith("Meeting topic: Sprint planning.", context);
        Assert.Contains("Meeting context: ", context);
        Assert.True(context.Length <= 560);
    }

    [Fact]
    public void MergeTerms_WorkspaceTermAlwaysWinsOverGlobalTermWithSameKey()
    {
        var workspaceTerms = new List<PromptTerm> { new("architect", "kiến trúc sư", 5) };
        var globalTerms = new List<PromptTerm> { new("architect", "architect", 8) };

        var (merged, droppedAsOverridden, droppedAsOverBudget) =
            GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 60);

        var term = Assert.Single(merged);
        Assert.Equal("kiến trúc sư", term.Target);
        Assert.Equal(1, droppedAsOverridden);
        Assert.Equal(0, droppedAsOverBudget);
    }

    [Fact]
    public void MergeTerms_OverrideMatchIsCaseAndWhitespaceInsensitive()
    {
        var workspaceTerms = new List<PromptTerm> { new("  Sprint  ", "sờ-prin", 5) };
        var globalTerms = new List<PromptTerm> { new("sprint", "sprint", 8) };

        var (merged, droppedAsOverridden, _) =
            GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 60);

        Assert.Single(merged);
        Assert.Equal(1, droppedAsOverridden);
    }

    [Fact]
    public void MergeTerms_KeepsBothTermsWhenKeysDiffer()
    {
        var workspaceTerms = new List<PromptTerm> { new("ARR", "doanh thu định kỳ", 5) };
        var globalTerms = new List<PromptTerm> { new("architect", "architect", 8) };

        var (merged, droppedAsOverridden, droppedAsOverBudget) =
            GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 60);

        Assert.Equal(2, merged.Count);
        Assert.Equal(0, droppedAsOverridden);
        Assert.Equal(0, droppedAsOverBudget);
    }

    [Fact]
    public void MergeTerms_WorkspaceTermsFillBudgetFirstThenGlobalTermsFillTheRest()
    {
        var workspaceTerms = new List<PromptTerm>
        {
            new("term-a", "a", 1),
            new("term-b", "b", 1),
        };
        var globalTerms = new List<PromptTerm>
        {
            new("term-c", "c", 9),
            new("term-d", "d", 8),
            new("term-e", "e", 7),
        };

        var (merged, droppedAsOverridden, droppedAsOverBudget) =
            GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 3);

        Assert.Equal(3, merged.Count);
        Assert.Equal(new[] { "term-a", "term-b", "term-c" }, merged.ConvertAll(t => t.Source));
        Assert.Equal(0, droppedAsOverridden);
        Assert.Equal(2, droppedAsOverBudget);
    }

    [Fact]
    public void MergeTerms_GlobalTermsOrderedByPriorityDescendingWhenFillingBudget()
    {
        var workspaceTerms = new List<PromptTerm>();
        var globalTerms = new List<PromptTerm>
        {
            new("low", "low", 1),
            new("high", "high", 9),
            new("mid", "mid", 5),
        };

        var (merged, _, _) = GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 60);

        Assert.Equal(new[] { "high", "mid", "low" }, merged.ConvertAll(t => t.Source));
    }

    [Fact]
    public void MergeTerms_WorkspaceTermsExceedingMaxTermsAreTrimmedByPriority()
    {
        var workspaceTerms = new List<PromptTerm>
        {
            new("ws-low", "l", 1),
            new("ws-high", "h", 10),
            new("ws-mid", "m", 5),
        };
        var globalTerms = new List<PromptTerm> { new("global-1", "g1", 9) };

        var (merged, droppedAsOverridden, droppedAsOverBudget) =
            GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 2);

        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { "ws-high", "ws-mid" }, merged.ConvertAll(t => t.Source));
        Assert.Equal(0, droppedAsOverridden);
        Assert.Equal(2, droppedAsOverBudget); // 1 ws-low dropped + 1 global-1 dropped
    }

    [Fact]
    public void MergeTerms_EmptyInputsReturnEmptyMerge()
    {
        var (merged, droppedAsOverridden, droppedAsOverBudget) =
            GlossaryStartedEventConsumer.MergeTerms(new List<PromptTerm>(), new List<PromptTerm>(), maxTerms: 60);

        Assert.Empty(merged);
        Assert.Equal(0, droppedAsOverridden);
        Assert.Equal(0, droppedAsOverBudget);
    }

    [Fact]
    public void BuildSttKeywords_UsesOnlyWorkspaceTermsAndBoundsProviderBias()
    {
        var workspaceTerms = new List<PromptTerm>
        {
            new("workspace-low", "workspace-low-vi", 1),
            new("workspace-high", "workspace-high-vi", 10),
            new("workspace-mid", "workspace-mid-vi", 5),
        };

        var keywords = GlossaryStartedEventConsumer.BuildSttKeywords(
            workspaceTerms,
            globalTerms: new List<PromptTerm>(),
            maxKeywords: 4,
            maxGlobalKeywords: 3);

        Assert.Equal(
            new[] { "workspace-high", "workspace-high-vi", "workspace-mid", "workspace-mid-vi" },
            keywords);
        Assert.DoesNotContain("architecture", keywords);
    }

    /// <summary>
    /// WT-426 — a global term must not take a slot from a workspace term.
    ///
    /// The call site used to pass `merged`, so a high-priority GLOBAL term outranked a
    /// workspace one in a budget of ten. Global terms describe what somebody on the platform
    /// says, usually not the people in this room, so they are hallucination surface with no
    /// upside — and on a noisy production meeting the recogniser reached for exactly them,
    /// emitting "WarpTalk, WarpBot, Codex." as an utterance nobody spoke.
    /// </summary>
    [Fact]
    public void BuildSttKeywords_LetsWorkspaceTermsWinTheBudgetOverHigherPriorityGlobals()
    {
        var workspaceTerms = new List<PromptTerm> { new("Warpspace", "Warpspace", 1) };
        var globalTerms = new List<PromptTerm> { new("Kubernetes", "Kubernetes", 99) };

        var keywords = GlossaryStartedEventConsumer.BuildSttKeywords(
            workspaceTerms, globalTerms, maxKeywords: 1, maxGlobalKeywords: 3);

        Assert.Equal(new[] { "Warpspace" }, keywords);
    }

    /// <summary>
    /// The negative control for the test above. Globals earned their place — "Codex" came back
    /// as "cô đích" without them — so narrowing the budget must not evict them entirely.
    /// </summary>
    [Fact]
    public void BuildSttKeywords_StillCarriesGlobalTermsWhenThereIsRoom()
    {
        var workspaceTerms = new List<PromptTerm> { new("Warpspace", "Warpspace", 1) };
        var globalTerms = new List<PromptTerm> { new("Codex", "Codex", 5) };

        var keywords = GlossaryStartedEventConsumer.BuildSttKeywords(
            workspaceTerms, globalTerms, maxKeywords: 10, maxGlobalKeywords: 3);

        Assert.Contains("Warpspace", keywords);
        Assert.Contains("Codex", keywords);
    }

    [Fact]
    public void BuildSttKeywords_BoundsHowMuchOfTheBudgetGlobalTermsMayTake()
    {
        // Room for ten, but a platform glossary could supply hundreds. Without its own ceiling a
        // workspace with two terms of its own would hand the other eight slots to terms nobody in
        // the room is going to say.
        var workspaceTerms = new List<PromptTerm> { new("Warpspace", "Warpspace", 1) };
        var globalTerms = Enumerable.Range(0, 20)
            .Select(index => new PromptTerm($"Globalterm{index}", $"Globalterm{index}", index))
            .ToList();

        var keywords = GlossaryStartedEventConsumer.BuildSttKeywords(
            workspaceTerms, globalTerms, maxKeywords: 10, maxGlobalKeywords: 3);

        Assert.Equal(4, keywords.Count);
        Assert.Contains("Warpspace", keywords);
        Assert.Equal(3, keywords.Count(k => k.StartsWith("Globalterm", StringComparison.Ordinal)));
    }

    [Fact]
    public void BuildSttKeywords_NeverExceedsTheOverallBudget()
    {
        // The two ceilings must compose, not add. A list bigger than the writer intends is the
        // shape of the bug: the recogniser resolves ambiguity into whatever it was handed.
        var workspaceTerms = Enumerable.Range(0, 20)
            .Select(index => new PromptTerm($"Workspaceterm{index}", $"Workspaceterm{index}", index))
            .ToList();
        var globalTerms = Enumerable.Range(0, 20)
            .Select(index => new PromptTerm($"Globalterm{index}", $"Globalterm{index}", index))
            .ToList();

        var keywords = GlossaryStartedEventConsumer.BuildSttKeywords(
            workspaceTerms, globalTerms, maxKeywords: 5, maxGlobalKeywords: 3);

        Assert.Equal(5, keywords.Count);
    }

    [Theory]
    [InlineData("architect", "architect")]
    [InlineData("  architect  ", "architect")]
    [InlineData("Architect", "architect")]
    [InlineData("full  stack", "full stack")]
    public void NormalizeKey_TrimsLowercasesAndCollapsesWhitespace(string input, string expected)
    {
        Assert.Equal(expected, GlossaryStartedEventConsumer.NormalizeKey(input));
    }

    [Theory]
    // Long enough to be a specific hint rather than a fragment of ordinary speech.
    [InlineData("Codex", true)]
    [InlineData("Kubernetes", true)]
    [InlineData("cơ sở dữ liệu", true)]
    // Acronyms are short by nature and are exactly what is worth biasing.
    [InlineData("AI", true)]
    [InlineData("QA", true)]
    [InlineData("gRPC", true)]
    [InlineData("iOS", true)]
    // Short, capital-less strings match fragments of ordinary speech constantly and
    // cost accuracy on every word NOT in the bias list.
    [InlineData("và", false)]
    [InlineData("là", false)]
    [InlineData("of", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsUsefulSttKeyword_RejectsShortNonAcronyms(string value, bool expected)
    {
        Assert.Equal(expected, GlossaryStartedEventConsumer.IsUsefulSttKeyword(value));
    }

    [Fact]
    public void BuildSttKeywords_DropsShortTermsSoProperNounsKeepTheBudget()
    {
        // The budget is small and both sides of every pair compete for it, so a couple of
        // two-letter targets used to be able to push out the proper nouns the list exists
        // for — "Codex", the term whose absence produced "cô đích" in a real rehearsal.
        var terms = new List<PromptTerm>
        {
            new("of", "và", 10),
            new("Codex", "Codex", 5),
            new("AI", "AI", 4),
        };

        var keywords = GlossaryStartedEventConsumer.BuildSttKeywords(
            terms, globalTerms: new List<PromptTerm>(), maxKeywords: 4, maxGlobalKeywords: 3);

        Assert.Contains("Codex", keywords);
        Assert.Contains("AI", keywords);
        Assert.DoesNotContain("of", keywords);
        Assert.DoesNotContain("và", keywords);
    }
}
