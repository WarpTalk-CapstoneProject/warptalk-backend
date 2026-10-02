using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WarpTalk.WorkspaceService.Application.Masking;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using WarpTalk.WorkspaceService.Infrastructure.Masking;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using static WarpTalk.WorkspaceService.Tests.MaskedDocumentFixtures;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// Rewriting a real .docx / .xlsx so the hidden values are gone from the file, wherever in the
/// package they sit.
/// </summary>
public class OpenXmlPiiMaskerTests
{
    private const string Name = "Nguyễn Văn A";
    private const string Phone = "0912345678";
    private const string Email = "van.a@example.com";

    private static readonly PiiValueMatcher Matcher = new(new[]
    {
        new PiiReplacement(Name, "[PII_REDACTED]"),
        new PiiReplacement(Phone, "[PHONE_REDACTED]"),
        new PiiReplacement(Email, "[EMAIL_REDACTED]")
    });

    private static async Task<string> ExtractAsync(byte[] file, string extension)
    {
        using var stream = new MemoryStream(file, writable: false);
        return (await new DocumentTextExtractor().ExtractTextAsync(stream, extension)).FullText;
    }

    private static string Everything(byte[] file, string extension) =>
        string.Join("\n", OpenXmlPiiMasker.CollectText(file, extension, OpenXmlPiiMasker.TextScope.Everything));

    [Fact]
    public async Task Docx_AValueSplitAcrossSeveralRuns_IsHidden()
    {
        // "Nguyễn Văn A" typed once, then partly re-formatted: three runs, none of which holds it.
        var docx = Docx(new OpenXmlElement[] { Paragraph("Contact Ngu", "yễn Văn", " A before Friday.") });
        Assert.Contains(Name, await ExtractAsync(docx, ".docx"));

        var outcome = OpenXmlPiiMasker.Mask(docx, ".docx", Matcher);

        Assert.False(outcome.UnsupportedContent);
        Assert.Equal("Contact [PII_REDACTED] before Friday.", (await ExtractAsync(outcome.Content!, ".docx")).Trim());
    }

    [Fact]
    public async Task Docx_TwoValuesInOneParagraph_SharingARun_AreBothHidden()
    {
        var docx = Docx(new OpenXmlElement[] { Paragraph("Call 09123", "45678 or write to van.a@", "example.com please") });

        var outcome = OpenXmlPiiMasker.Mask(docx, ".docx", Matcher);

        Assert.Equal(
            "Call [PHONE_REDACTED] or write to [EMAIL_REDACTED] please",
            (await ExtractAsync(outcome.Content!, ".docx")).Trim());
    }

    [Fact]
    public async Task Docx_TableCell_Header_Footer_AndFootnote_AreHidden_AndTheTableSurvives()
    {
        var docx = Docx(
            new OpenXmlElement[]
            {
                Paragraph("Staff list"),
                Table(new[] { "Name", "Phone" }, new[] { Name, Phone })
            },
            header: $"Prepared by {Name}",
            footer: $"Questions: {Email}",
            footnote: $"Mobile of the author: {Phone}");

        var outcome = OpenXmlPiiMasker.Mask(docx, ".docx", Matcher);
        var masked = outcome.Content!;

        var body = await ExtractAsync(masked, ".docx");
        Assert.Contains("[PII_REDACTED]", body);
        Assert.Contains("[PHONE_REDACTED]", body);

        var everything = Everything(masked, ".docx");
        Assert.Contains("Prepared by [PII_REDACTED]", everything);
        Assert.Contains("Questions: [EMAIL_REDACTED]", everything);
        Assert.Contains("Mobile of the author: [PHONE_REDACTED]", everything);
        Assert.False(Matcher.ContainsAny(everything));

        using var reopened = WordprocessingDocument.Open(new MemoryStream(masked), false);
        var table = Assert.Single(reopened.MainDocumentPart!.Document!.Body!.Elements<W.Table>());
        Assert.Equal(2, table.Elements<W.TableRow>().Count());
        Assert.Equal(4, table.Descendants<W.TableCell>().Count());
    }

    [Fact]
    public void Docx_Comments_Authors_Properties_AndPictures_DoNotLeaveInTheMaskedCopy()
    {
        var docx = Docx(
            new OpenXmlElement[] { Paragraph("Body text") },
            comment: $"Ask {Name} about this",
            author: Name,
            withImage: true,
            hyperlink: $"mailto:{Email}");

        var originalText = Everything(docx, ".docx");
        Assert.Contains(Name, originalText);
        Assert.Contains(Email, originalText);

        var masked = OpenXmlPiiMasker.Mask(docx, ".docx", Matcher).Content!;

        Assert.False(Matcher.ContainsAny(Everything(masked, ".docx")));

        using var reopened = WordprocessingDocument.Open(new MemoryStream(masked), false);
        var main = reopened.MainDocumentPart!;
        Assert.Null(main.WordprocessingCommentsPart);
        Assert.Empty(main.Document!.Descendants<W.CommentReference>());
        Assert.Empty(main.Document.Descendants<W.CommentRangeStart>());
        Assert.True(string.IsNullOrEmpty(reopened.PackageProperties.Creator));
        Assert.True(string.IsNullOrEmpty(reopened.PackageProperties.LastModifiedBy));
        Assert.Equal("about:blank", Assert.Single(main.HyperlinkRelationships).Uri.OriginalString);

        // The picture is still a part — the layout that referred to it is intact — but its bytes
        // are no longer the upload's.
        using var pixels = new MemoryStream();
        using (var stream = Assert.Single(main.ImageParts).GetStream())
        {
            stream.CopyTo(pixels);
        }

        Assert.DoesNotContain(Phone, Encoding.ASCII.GetString(pixels.ToArray()));
    }

    [Fact]
    public void Docx_TrackedDeletions_AreRemoved()
    {
        var deleted = new W.Paragraph(
            new W.Run(new W.Text("Kept. ")),
            new W.DeletedRun(new W.Run(new W.DeletedText($"Removed earlier: {Phone}"))) { Id = "1", Author = Name });
        var docx = Docx(new OpenXmlElement[] { deleted });

        var masked = OpenXmlPiiMasker.Mask(docx, ".docx", Matcher).Content!;

        var everything = Everything(masked, ".docx");
        Assert.DoesNotContain(Phone, everything);
        Assert.DoesNotContain("Removed earlier", everything);
        Assert.Contains("Kept.", everything);
    }

    [Fact]
    public void Docx_TextOutsideTheBody_IsWhatTheSecondScanIsGiven()
    {
        var docx = Docx(
            new OpenXmlElement[] { Paragraph($"Body mentions {Phone}") },
            header: $"Prepared by {Name}",
            footer: "Page footer");

        var outside = OpenXmlPiiMasker.CollectText(docx, ".docx", OpenXmlPiiMasker.TextScope.OutsideExtractedText);

        Assert.Contains($"Prepared by {Name}", outside);
        Assert.Contains("Page footer", outside);
        Assert.DoesNotContain(outside, text => text.Contains("Body mentions"));
    }

    [Fact]
    public async Task Xlsx_SharedStringAndNumberCells_AreHidden_AndOtherCellsKept()
    {
        var xlsx = Xlsx(
            "Staff",
            new object[] { "Name", "Phone", "Salary" },
            new object[] { Name, Phone, 1500 },
            new object[] { "Office line", 912345678, 20 });

        var matcher = new PiiValueMatcher(new[]
        {
            new PiiReplacement(Name, "[PII_REDACTED]"),
            new PiiReplacement(Phone, "[PHONE_REDACTED]"),
            new PiiReplacement("912345678", "[ID_REDACTED]")
        });

        var outcome = OpenXmlPiiMasker.Mask(xlsx, ".xlsx", matcher);

        Assert.False(outcome.UnsupportedContent);
        var text = await ExtractAsync(outcome.Content!, ".xlsx");
        Assert.Contains("[PII_REDACTED] [PHONE_REDACTED] 1500", text);
        Assert.Contains("Office line [ID_REDACTED] 20", text);
        Assert.Contains("Name Phone Salary", text);
        Assert.False(matcher.ContainsAny(Everything(outcome.Content!, ".xlsx")));
    }

    [Fact]
    public void Xlsx_AValueInASheetName_IsRefused_RatherThanRenamed()
    {
        var xlsx = Xlsx(Name, new object[] { "anything" });

        var outcome = OpenXmlPiiMasker.Mask(xlsx, ".xlsx", Matcher);

        Assert.True(outcome.UnsupportedContent);
        Assert.Null(outcome.Content);
    }

    [Fact]
    public void Xlsx_SheetNames_AreOutsideTheExtractedText()
    {
        var xlsx = Xlsx("Payroll of Trần Thị B", new object[] { "cell text" });

        var outside = OpenXmlPiiMasker.CollectText(xlsx, ".xlsx", OpenXmlPiiMasker.TextScope.OutsideExtractedText);

        Assert.Contains("Payroll of Trần Thị B", outside);
        Assert.DoesNotContain("cell text", outside);
    }
}
