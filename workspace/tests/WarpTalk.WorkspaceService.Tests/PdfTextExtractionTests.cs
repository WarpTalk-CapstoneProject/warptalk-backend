using System.IO;
using System.Threading.Tasks;
using iTextSharp.text;
using iTextSharp.text.pdf;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class PdfTextExtractionTests
{
    private const string Prose =
        "The air handling unit on floor seven is inspected every month. A technician checks the filters, "
        + "records the supply temperature and reports anything unusual to the operations desk.";

    private static byte[] BuildPdf(string text)
    {
        using var ms = new MemoryStream();
        var doc = new Document();
        var writer = PdfWriter.GetInstance(doc, ms);
        doc.Open();
        doc.Add(new Paragraph(text));
        doc.Close();
        writer.Close();
        return ms.ToArray();
    }

    [Fact]
    public async Task Pdf_ShouldYieldTheReadableText_NotTheContentStreamOperators()
    {
        using var stream = new MemoryStream(BuildPdf(Prose));

        var content = await new DocumentTextExtractor().ExtractTextAsync(stream, ".pdf");

        Assert.Contains("air handling unit", content.FullText);
        Assert.Contains("operations desk", content.FullText);
        // The operators the old tokeniser path leaked into the text.
        Assert.DoesNotContain(" Tj", content.FullText);
        Assert.DoesNotContain("BT ", content.FullText);
        Assert.False(ExtractedTextQuality.LooksUnreadable(content.FullText));
    }

    [Fact]
    public void Prose_IsReadable_IncludingVietnamese()
    {
        Assert.False(ExtractedTextQuality.LooksUnreadable(Prose));
        Assert.False(ExtractedTextQuality.LooksUnreadable(
            "Hệ thống điều hòa không khí của tòa nhà được kiểm tra định kỳ hằng tháng bởi đội kỹ thuật, "
            + "mọi bất thường đều được ghi lại và báo cáo cho bộ phận vận hành."));
    }

    [Fact]
    public void OperatorSoup_IsUnreadable()
    {
        const string soup = "Td Tj Td Tj Td 02 Tj Td 0S Tj Td 0H Tj Td 0U Tj BDC m c c c c c l EMC BT Tf Tm Td Tj "
            + "Td Tj Td Tj ET EMC BDC BT Tf Tm Tj Td Tj Td Tj Td Tj Td Tj Td Tj";
        Assert.True(ExtractedTextQuality.LooksUnreadable(soup));
    }

    [Fact]
    public void GlyphCodeNoise_IsUnreadable()
    {
        // Long enough to be judged at all (text under ~80 visible characters is let through).
        const string chunk = "D$0$3D$0 ,0$S$04 $0$$0Q$0 0Q0S0G 06$0 $%$0&$ 0$0$0$ #$%$&$ 0@0@0@ $$0$ 0%0%0% 0$0$0$0 ";
        Assert.True(ExtractedTextQuality.LooksUnreadable(chunk + chunk));
    }

    [Fact]
    public void ShortOrEmptyText_IsNotJudged()
    {
        Assert.False(ExtractedTextQuality.LooksUnreadable(null));
        Assert.False(ExtractedTextQuality.LooksUnreadable("  "));
        Assert.False(ExtractedTextQuality.LooksUnreadable("Td Tj"));
    }
}
