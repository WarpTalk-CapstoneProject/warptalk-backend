using System;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// Small real Office files for the masking tests, built with the same SDK the masker reads them
/// with. Real packages rather than stubs: what is under test is what survives a round trip
/// through a .docx, and a stub has no runs to split a name across.
/// </summary>
internal static class MaskedDocumentFixtures
{
    public static W.Paragraph Paragraph(params string[] runs) =>
        new(runs.Select(text => new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve })));

    /// <summary>A .docx whose body is the given elements, with optional header, footer, footnote, comment and image.</summary>
    public static byte[] Docx(
        OpenXmlElement[] body,
        string? header = null,
        string? footer = null,
        string? footnote = null,
        string? comment = null,
        string? author = null,
        bool withImage = false,
        string? hyperlink = null)
    {
        using var buffer = new MemoryStream();
        using (var word = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            var documentBody = new W.Body(body.Select(e => e.CloneNode(true)));
            main.Document = new W.Document(documentBody);

            if (header is not null)
            {
                var part = main.AddNewPart<HeaderPart>();
                part.Header = new W.Header(Paragraph(header));
            }

            if (footer is not null)
            {
                var part = main.AddNewPart<FooterPart>();
                part.Footer = new W.Footer(Paragraph(footer));
            }

            if (footnote is not null)
            {
                var part = main.AddNewPart<FootnotesPart>();
                part.Footnotes = new W.Footnotes(new W.Footnote(Paragraph(footnote)) { Id = 1 });
            }

            if (comment is not null)
            {
                var part = main.AddNewPart<WordprocessingCommentsPart>();
                part.Comments = new W.Comments(
                    new W.Comment(Paragraph(comment)) { Id = "0", Author = author ?? "Reviewer" });
                documentBody.Append(new W.Paragraph(
                    new W.CommentRangeStart { Id = "0" },
                    new W.Run(new W.Text("commented")),
                    new W.CommentRangeEnd { Id = "0" },
                    new W.Run(new W.CommentReference { Id = "0" })));
            }

            if (withImage)
            {
                var image = main.AddImagePart(ImagePartType.Png);
                using var pixels = new MemoryStream(Encoding.ASCII.GetBytes("NOT-A-REAL-PICTURE-0912345678"));
                image.FeedData(pixels);
            }

            if (hyperlink is not null)
            {
                var relationship = main.AddHyperlinkRelationship(new Uri(hyperlink), true);
                documentBody.Append(new W.Paragraph(
                    new W.Hyperlink(new W.Run(new W.Text("write to us"))) { Id = relationship.Id }));
            }

            word.PackageProperties.Creator = author;
            word.PackageProperties.LastModifiedBy = author;
        }

        return buffer.ToArray();
    }

    public static W.Table Table(params string[][] rows) =>
        new(rows.Select(row => new W.TableRow(row.Select(cell => new W.TableCell(Paragraph(cell))))));

    /// <summary>An .xlsx with one sheet. Strings go through the shared string table, numbers stay numbers.</summary>
    public static byte[] Xlsx(string sheetName, params object[][] rows)
    {
        using var buffer = new MemoryStream();
        using (var workbook = SpreadsheetDocument.Create(buffer, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = workbook.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();

            var shared = workbookPart.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new S.SharedStringTable();

            var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var data = new S.SheetData();
            uint rowIndex = 1;
            foreach (var row in rows)
            {
                var sheetRow = new S.Row { RowIndex = rowIndex++ };
                foreach (var value in row)
                {
                    if (value is string text)
                    {
                        shared.SharedStringTable.AppendChild(new S.SharedStringItem(new S.Text(text)));
                        var index = shared.SharedStringTable.Elements<S.SharedStringItem>().Count() - 1;
                        sheetRow.Append(new S.Cell
                        {
                            DataType = S.CellValues.SharedString,
                            CellValue = new S.CellValue(index.ToString())
                        });
                    }
                    else
                    {
                        sheetRow.Append(new S.Cell { CellValue = new S.CellValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!) });
                    }
                }

                data.Append(sheetRow);
            }

            sheetPart.Worksheet = new S.Worksheet(data);
            workbookPart.Workbook.AppendChild(new S.Sheets(new S.Sheet
            {
                Id = workbookPart.GetIdOfPart(sheetPart),
                SheetId = 1,
                Name = sheetName
            }));
        }

        return buffer.ToArray();
    }
}
