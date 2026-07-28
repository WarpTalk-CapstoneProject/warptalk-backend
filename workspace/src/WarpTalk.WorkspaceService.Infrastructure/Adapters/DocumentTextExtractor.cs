using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Spreadsheet;
using iTextSharp.text.pdf;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;

namespace WarpTalk.WorkspaceService.Infrastructure.Adapters;

/// <summary>
/// Infrastructure service using iTextSharp PrTokeniser for PDF extraction and OpenXml for DOCX/XLSX extraction.
/// </summary>
public class DocumentTextExtractor : IDocumentTextExtractor
{
    public async Task<ExtractedDocumentContent> ExtractTextAsync(Stream fileStream, string fileExtension, CancellationToken ct = default)
    {
        var ext = fileExtension.ToLower().TrimStart('.');
        var result = new ExtractedDocumentContent();

        if (ext == "pdf")
        {
            return await Task.Run(() =>
            {
                var fullTextBuilder = new StringBuilder();
                var reader = new PdfReader(fileStream);
                try
                {
                    for (int page = 1; page <= reader.NumberOfPages; page++)
                    {
                        var pageTextBuilder = new StringBuilder();
                        var contentBytes = reader.GetPageContent(page);
                        if (contentBytes != null && contentBytes.Length > 0)
                        {
                            var tokenizer = new PrTokeniser(new RandomAccessFileOrArray(contentBytes));
                            while (tokenizer.NextToken())
                            {
                                var tokenType = tokenizer.TokenType;
                                var val = tokenizer.StringValue;
                                if (tokenType == PrTokeniser.TK_STRING || tokenType == PrTokeniser.TK_OTHER)
                                {
                                    if (!string.IsNullOrWhiteSpace(val) && val.Any(char.IsLetterOrDigit))
                                    {
                                        pageTextBuilder.Append(val).Append(' ');
                                    }
                                }
                            }
                        }

                        var pageText = pageTextBuilder.ToString().Trim();
                        result.Pages.Add(new ExtractedPage { PageNumber = page, Text = pageText });
                        fullTextBuilder.AppendLine(pageText);
                    }
                }
                finally
                {
                    reader.Close();
                }
                result.FullText = fullTextBuilder.ToString();
                return result;
            }, ct);
        }
        else if (ext == "docx")
        {
            return await Task.Run(() =>
            {
                var textBuilder = new StringBuilder();
                using (var wordDoc = WordprocessingDocument.Open(fileStream, false))
                {
                    var body = wordDoc.MainDocumentPart?.Document.Body;
                    if (body != null)
                    {
                        foreach (var paragraph in body.Descendants<Paragraph>())
                        {
                            textBuilder.AppendLine(paragraph.InnerText);
                        }
                    }
                }
                var fullText = textBuilder.ToString();
                result.FullText = fullText;
                result.Pages.Add(new ExtractedPage { PageNumber = 1, Text = fullText });
                return result;
            }, ct);
        }
        else if (ext == "xlsx")
        {
            return await Task.Run(() =>
            {
                var fullTextBuilder = new StringBuilder();
                using (var spreadsheetDoc = SpreadsheetDocument.Open(fileStream, false))
                {
                    var workbookPart = spreadsheetDoc.WorkbookPart;
                    if (workbookPart != null)
                      {
                        var sharedStringTable = workbookPart.SharedStringTablePart?.SharedStringTable;
                        var sheetsList = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? new List<Sheet>();

                        foreach (var worksheetPart in workbookPart.WorksheetParts)
                        {
                            var rId = workbookPart.GetIdOfPart(worksheetPart);
                            var sheet = sheetsList.FirstOrDefault(s => s.Id?.Value == rId);
                            var sheetName = sheet?.Name?.Value ?? "Sheet " + (result.Sheets.Count + 1);

                            var extractedSheet = new ExtractedSheet { SheetName = sheetName };
                            var sheetData = worksheetPart.Worksheet?.GetFirstChild<SheetData>();

                            if (sheetData != null)
                            {
                                foreach (var row in sheetData.Elements<Row>())
                                {
                                    var rowTexts = new List<string>();
                                    foreach (var cell in row.Elements<Cell>())
                                    {
                                        var value = cell.CellValue?.Text ?? cell.InnerText;
                                        if (!string.IsNullOrEmpty(value))
                                        {
                                            if (cell.DataType != null && cell.DataType.Value == CellValues.SharedString && sharedStringTable != null)
                                            {
                                                if (int.TryParse(value, out var index))
                                                {
                                                    if (index >= 0 && index < sharedStringTable.ChildElements.Count)
                                                    {
                                                        value = sharedStringTable.ElementAt(index).InnerText;
                                                    }
                                                }
                                            }
                                            rowTexts.Add(value);
                                        }
                                    }
                                    if (rowTexts.Count > 0)
                                    {
                                        extractedSheet.Rows.Add(rowTexts);
                                        fullTextBuilder.AppendLine(string.Join(" ", rowTexts));
                                    }
                                }
                            }
                            result.Sheets.Add(extractedSheet);
                        }
                    }
                }
                result.FullText = fullTextBuilder.ToString();
                return result;
            }, ct);
        }
        else if (ext == "csv")
        {
            using var reader = new StreamReader(fileStream, Encoding.UTF8);
            var fullTextBuilder = new StringBuilder();
            var sheet = new ExtractedSheet { SheetName = "CSV Data" };

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                fullTextBuilder.AppendLine(line);

                var cells = ParseCsvRow(line);
                if (cells.Count > 0)
                {
                    sheet.Rows.Add(cells);
                }
            }

            var fullText = fullTextBuilder.ToString();
            result.FullText = fullText;
            if (sheet.Rows.Count > 0)
            {
                result.Sheets.Add(sheet);
            }
            result.Pages.Add(new ExtractedPage { PageNumber = 1, Text = fullText });
            return result;
        }
        else
        {
            // Plain text (txt, md, json) UTF-8 fallback
            using var reader = new StreamReader(fileStream, Encoding.UTF8);
            var fullText = await reader.ReadToEndAsync(ct);
            result.FullText = fullText;
            result.Pages.Add(new ExtractedPage { PageNumber = 1, Text = fullText });
            return result;
        }
    }

    private static List<string> ParseCsvRow(string row)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(row)) return result;

        var inQuotes = false;
        var sb = new StringBuilder();

        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < row.Length && row[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString().Trim());
        return result;
    }
}
