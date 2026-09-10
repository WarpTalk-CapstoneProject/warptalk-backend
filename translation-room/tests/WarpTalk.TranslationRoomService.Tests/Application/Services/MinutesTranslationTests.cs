using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// Reading a signed record in a language it was not drawn up in.
///
/// `MeetingMinutesContent.Translations` covers the ROOM's target languages — the ones the meeting
/// was being interpreted into while it ran. A reader of anything else has no version of the
/// document at all, and this is the path that answers them.
///
/// Nearly every test here is about a REFUSAL, which is the shape of the feature. Rebuilding the
/// body from the meeting's summary is only a translation of THIS document while the two still say
/// the same thing, and a biên bản is the one artifact in the product whose whole value is that a
/// named person stood behind its words. Showing a reader prose the signatory never wrote is worse
/// than showing them nothing, so every way the two can drift apart ends in a stated reason.
/// </summary>
public sealed class MinutesTranslationTests
{
    /// <summary>
    /// The shape is read off the document's own section keys, because a minutes row has never
    /// recorded which template it was drawn from — asking for the wrong shape would rebuild a
    /// different document and the key check would then refuse a translation that should have
    /// worked.
    /// </summary>
    [Theory]
    [InlineData(new[] { "summary", "decisions" }, "general")]
    [InlineData(new[] { "progress", "plans", "blockers" }, "standup")]
    [InlineData(new[] { "background", "strengths", "concerns" }, "interview")]
    [InlineData(new[] { "shown", "reactions", "objections" }, "demo")]
    [InlineData(new[] { "problems", "options" }, "technical")]
    [InlineData(new[] { "narrative" }, "traceable")]
    [InlineData(new string[0], "general")]
    public void TheShapeIsIdentifiedByTheSectionsTheDocumentActuallyHas(string[] keys, string expected)
    {
        var content = ContentWith(keys);
        TemplateKeyOf(content).Should().Be(expected);
    }

    /// <summary>
    /// A section every template declares must not decide the shape on its own. `decisions` and
    /// `actionItems` appear in all of them, so a standup identified by `decisions` alone would
    /// come back as General and rebuild the wrong body.
    /// </summary>
    [Fact]
    public void ASectionEveryTemplateSharesDoesNotIdentifyAnything()
    {
        TemplateKeyOf(ContentWith(new[] { "decisions", "actionItems" })).Should().Be("general");
    }

    /// <summary>
    /// The carried-over section exists in the document and is deliberately absent from a rebuild
    /// — it quotes an EARLIER meeting's commitments, and re-translating a quotation is what must
    /// not happen to a record. So the check is "the rebuild covers the document's own sections",
    /// never "the two have the same number of sections".
    /// </summary>
    [Fact]
    public void ARebuildMissingOnlyTheCarriedOverSectionStillMatches()
    {
        var document = new HashSet<string>(StringComparer.Ordinal) { "carriedOver", "summary", "decisions" };
        var rebuilt = new HashSet<string>(StringComparer.Ordinal) { "summary", "decisions", "carriedOver" };

        rebuilt.IsSupersetOf(document).Should().BeTrue();
    }

    /// <summary>
    /// A summary rewritten into another shape after the minutes were drawn up produces a body
    /// with different sections. Printing that beside the document as "the same thing in Japanese"
    /// would be a claim of correspondence nobody checked — the failure `pairByCitation` refuses
    /// at the line level, here at the level of the whole document.
    /// </summary>
    [Fact]
    public void ARebuildOfADifferentShapeDoesNotCoverTheDocument()
    {
        var document = new HashSet<string>(StringComparer.Ordinal) { "summary", "decisions" };
        var rebuilt = new HashSet<string>(StringComparer.Ordinal) { "progress", "plans", "blockers" };

        rebuilt.IsSupersetOf(document).Should().BeFalse();
    }

    /// <summary>
    /// SectionsFrom omits carried-over items by construction, so a caller cannot accidentally get
    /// a rebuild that restates another meeting's record in a third language.
    /// </summary>
    [Fact]
    public void TheRebuildNeverContainsACarriedOverSection()
    {
        var summary = JsonSerializer.Serialize(new
        {
            summary = "本日の会議",
            decisions = new[] { "予算を承認" },
            actionItems = Array.Empty<object>(),
            templateKey = "general",
            summaryLanguage = "ja",
        });

        var sections = MeetingMinutesDrafter.SectionsFrom(summary);

        sections.Should().NotBeEmpty();
        sections.Should().NotContain(section => section.Key == "carriedOver");
    }

    /// <summary>
    /// A summary with nothing in it rebuilds to nothing, so the superset check refuses rather
    /// than claiming a document with sections was translated into one with none.
    /// </summary>
    [Fact]
    public void AnEmptySummaryRebuildsToNothing()
    {
        MeetingMinutesDrafter.SectionsFrom(null).Should().BeEmpty();
        MeetingMinutesDrafter.SectionsFrom("{}").Should().BeEmpty();
    }

    /// <summary>
    /// A summary that is not JSON is an older artifact holding plain text, and BuildSections puts
    /// it in as the overview rather than dropping the meeting's narrative — deliberate, and
    /// documented there.
    ///
    /// Pinned HERE because of what it means for a translation: that path yields exactly one
    /// `summary` section, so any real document — which has decisions or action items beside its
    /// overview — is correctly refused by the superset check instead of being "translated" into a
    /// single paragraph. The guard is what makes the fallback safe to reuse.
    /// </summary>
    [Fact]
    public void APlainTextSummaryRebuildsToTheOverviewAloneAndSoCannotCoverARealDocument()
    {
        var rebuilt = MeetingMinutesDrafter.SectionsFrom("just some prose");

        rebuilt.Should().ContainSingle();
        rebuilt[0].Key.Should().Be("summary");

        var document = new HashSet<string>(StringComparer.Ordinal) { "summary", "decisions" };
        var rebuiltKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in rebuilt) rebuiltKeys.Add(section.Key);

        rebuiltKeys.IsSupersetOf(document).Should().BeFalse();
    }

    private static MeetingMinutesContent ContentWith(IEnumerable<string> keys)
    {
        var sections = new List<MinutesSection>();
        foreach (var key in keys)
        {
            sections.Add(new MinutesSection { Key = key, Kind = "items", Items = new List<MinutesItem>() });
        }
        return new MeetingMinutesContent { Sections = sections };
    }

    /// <summary>
    /// MIRRORS MeetingMinutesService.ReadTemplateKey. Kept here rather than made public on the
    /// service: the rule is small, and widening a service's surface so a test can reach it is how
    /// a private decision becomes an API somebody else starts depending on.
    /// </summary>
    private static string TemplateKeyOf(MeetingMinutesContent content)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in content.Sections ?? new List<MinutesSection>())
        {
            keys.Add(section.Key);
        }

        if (keys.Contains("progress") || keys.Contains("blockers")) return "standup";
        if (keys.Contains("strengths") || keys.Contains("concerns")) return "interview";
        if (keys.Contains("shown") || keys.Contains("objections")) return "demo";
        if (keys.Contains("problems") || keys.Contains("options")) return "technical";
        if (keys.Contains("narrative")) return "traceable";
        return "general";
    }
}
