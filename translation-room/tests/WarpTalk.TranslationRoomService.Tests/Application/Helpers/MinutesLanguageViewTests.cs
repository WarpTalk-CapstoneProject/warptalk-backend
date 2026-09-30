using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// WT-685: a biên bản is read and exported in one language at a time. The file that prompted
/// this carried [ja] under clause 3.1, [vi] under 3.2, and English under English tagged [en].
/// </summary>
public class MinutesLanguageViewTests
{
    private static MinutesSection Paragraph(string key, string text) =>
        new() { Key = key, Kind = "paragraph", Text = text };

    private static MinutesSection Items(string key, params string[] texts) =>
        new()
        {
            Key = key,
            Kind = "items",
            Items = texts.Select((text, index) => new MinutesItem { Text = text, AtMs = 1000 * (index + 1) }).ToList()
        };

    private static MeetingMinutesContent Document() => new()
    {
        PrimaryLanguage = "en",
        Sections = new List<MinutesSection>
        {
            Paragraph("summary", "The team reviewed the launch."),
            Items("decisions", "Launch on Friday")
        },
        Translations = new Dictionary<string, List<MinutesSection>>
        {
            ["en"] = new() { Paragraph("summary", "The team reviewed the launch.") },
            ["ja"] = new() { Paragraph("summary", "チームはローンチを確認した。"), Items("decisions", "金曜日にローンチ") },
            ["vi-VN"] = new() { Paragraph("summary", "Nhóm đã xem lại buổi ra mắt.") }
        }
    };

    [Fact]
    public void WithNoLanguageTheDocumentIsTheOriginalAndNothingElse()
    {
        var shaped = MinutesLanguageView.Shape(Document(), null, null);

        shaped.Translations.Should().BeNull();
        shaped.Sections[0].Text.Should().Be("The team reviewed the launch.");
    }

    [Fact]
    public void ChoosingTheOriginalsOwnLanguageIsTheOriginalToo()
    {
        MinutesLanguageView.Shape(Document(), "en-US", MinutesLanguageView.ModeBilingual)
            .Translations.Should().BeNull();
    }

    [Fact]
    public void MonoReplacesEverySectionWithThatLanguage()
    {
        var shaped = MinutesLanguageView.Shape(Document(), "ja", MinutesLanguageView.ModeMono);

        shaped.Translations.Should().BeNull();
        shaped.Sections[0].Text.Should().Be("チームはローンチを確認した。");
        shaped.Sections[1].Items!.Single().Text.Should().Be("金曜日にローンチ");
    }

    [Fact]
    public void AnUntranslatedSectionSaysSoInsteadOfBorrowingAnotherLanguage()
    {
        var shaped = MinutesLanguageView.Shape(Document(), "vi", MinutesLanguageView.ModeMono);

        shaped.Sections[0].Text.Should().Be("Nhóm đã xem lại buổi ra mắt.");
        shaped.Sections[1].Kind.Should().Be("paragraph");
        shaped.Sections[1].Text.Should().Be(MinutesLanguageView.UntranslatedNotice("vi"));
    }

    [Fact]
    public void BilingualIsTheOriginalPlusExactlyOneLanguage()
    {
        var shaped = MinutesLanguageView.Shape(Document(), "vi-VN", MinutesLanguageView.ModeBilingual);

        shaped.Sections[0].Text.Should().Be("The team reviewed the launch.");
        shaped.Translations.Should().NotBeNull();
        shaped.Translations!.Keys.Should().Equal("vi");
    }

    [Fact]
    public void AReadingGeneratedOnRequestWinsOverNothingStored()
    {
        var requested = new List<MinutesSection> { Paragraph("summary", "El equipo revisó el lanzamiento.") };

        var shaped = MinutesLanguageView.Shape(Document(), "es", MinutesLanguageView.ModeMono, requested);

        shaped.Sections[0].Text.Should().Be("El equipo revisó el lanzamiento.");
    }

    [Fact]
    public void CleaningDropsTheOriginalsLanguageAndFoldsRegionTags()
    {
        var cleaned = MinutesLanguageView.CleanTranslations(Document().Translations, "en");

        cleaned!.Keys.Should().BeEquivalentTo("ja", "vi");
    }

    [Fact]
    public void ShapingNeverChangesTheStoredDocument()
    {
        var document = Document();

        MinutesLanguageView.Shape(document, "ja", MinutesLanguageView.ModeMono);

        document.Sections[0].Text.Should().Be("The team reviewed the launch.");
        document.Translations!.Should().ContainKey("en");
    }

    [Fact]
    public void ANewDraftStoresNoTranslationOfItsOwnLanguageAndOneKeyPerLanguage()
    {
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            Title = "Launch review",
            SourceLanguage = "en",
            EndedAt = DateTime.UtcNow
        };
        const string summary = """
            {"summary":"Reviewed the launch.","summaryLanguage":"en",
             "translations":{"en":{"summary":"Reviewed the launch."},
                             "vi-VN":{"summary":"Đã xem lại buổi ra mắt."},
                             "vi":{"summary":"Bản thứ hai."}}}
            """;

        var content = JsonSerializer.Deserialize<MeetingMinutesContent>(
            MeetingMinutesDrafter.BuildContent(room, new List<TranslationRoomParticipant>(), summary))!;

        content.Translations!.Keys.Should().Equal("vi");
        content.Translations["vi"][0].Text.Should().Be("Đã xem lại buổi ra mắt.");
    }
}
