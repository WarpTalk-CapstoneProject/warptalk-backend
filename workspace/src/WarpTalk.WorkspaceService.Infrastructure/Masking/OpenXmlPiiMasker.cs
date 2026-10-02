using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WarpTalk.WorkspaceService.Application.Masking;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using M = DocumentFormat.OpenXml.Math;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WarpTalk.WorkspaceService.Infrastructure.Masking;

/// <summary>
/// Rewrites a .docx or .xlsx in place so that the values the security scan hid are gone from the
/// FILE, not just from its text — and reads every piece of text back out of one, so the result
/// can be checked.
/// </summary>
/// <remarks>
/// THE RULE THIS IS BUILT ON: nothing the scan did not read may leave in a masked copy.
///
/// A masked copy is handed to people who are not allowed the original, so what matters is not
/// only that the known values are replaced but that there is no text in the package that nobody
/// looked at. An Office file has a great deal of that. Each kind is dealt with one of four ways:
///
///   REWRITTEN   body text, tables, text boxes, headers, footers, footnotes, endnotes, chart and
///               diagram text, field codes, alt text, hyperlink targets, cells, shared strings.
///               A value split across several runs is found on the run-joined text and removed
///               from each run it touches.
///   REMOVED     comments and their authors, tracked deletions, custom XML, document properties
///               (author, company, last modified by), thumbnails, phonetic guides, sheet
///               headers/footers. These are either invisible in a normal read or metadata; a
///               reader of the masked copy loses nothing by their absence.
///   BLANKED     images and embedded objects. A picture cannot be scanned or masked, so its bytes
///               are replaced with an empty image and the layout around it stays.
///   REFUSED     anything that carries a second copy of the data in a shape this cannot rewrite —
///               pivot caches, macros, external links, imported HTML/RTF chunks. The answer is
///               "unsupported content" and no masked copy at all.
///
/// Text outside the body (headers, footers, footnotes, sheet names, chart text…) is NOT in the
/// extracted text the main scan read. <see cref="CollectText"/> with
/// <see cref="TextScope.OutsideExtractedText"/> returns exactly that remainder so the caller can
/// put it through the scan too before masking.
/// </remarks>
public static class OpenXmlPiiMasker
{
    public enum TextScope
    {
        /// <summary>Every piece of text in the package. What verification reads.</summary>
        Everything,

        /// <summary>Only text the document text extractor does not return. What the second scan reads.</summary>
        OutsideExtractedText
    }

    public sealed record Outcome(byte[]? Content, bool UnsupportedContent);

    private static readonly Outcome Unsupported = new(null, true);

    /// <summary>A 1×1 transparent PNG: what every picture becomes.</summary>
    private static readonly byte[] BlankImage = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>Parts that hold a second copy of the content in a form that cannot be rewritten here.</summary>
    private static readonly HashSet<string> RefusedPartTypes = new(StringComparer.Ordinal)
    {
        "VbaProjectPart", "VbaDataPart", "AlternativeFormatImportPart", "PivotTableCacheDefinitionPart",
        "PivotTableCacheRecordsPart", "PivotTablePart", "ExternalWorkbookPart", "ConnectionsPart",
        "QueryTablePart", "CustomXmlMappingsPart", "SlicerCachePart", "SlicersPart", "MacroSheetPart",
        "DialogsheetPart", "InternationalMacroSheetPart", "ExtendedPart", "TimeLineCachePart",
        "TimeLinePart", "CustomDataPropertiesPart", "CustomDataPart", "WorkbookRevisionHeaderPart",
        "WorkbookRevisionLogPart", "WorkbookUserDataPart", "MailMergeRecipientDataPart",
        "LegacyDiagramTextPart", "LegacyDiagramTextInfoPart", "RdRichValuePart", "ModelPart"
    };

    /// <summary>Parts that are dropped from a masked copy outright.</summary>
    private static readonly HashSet<string> RemovedPartTypes = new(StringComparer.Ordinal)
    {
        "CustomXmlPart", "CustomXmlPropertiesPart", "WordprocessingCommentsPart",
        "WordprocessingCommentsExPart", "WordprocessingCommentsIdsPart", "WordCommentsExtensiblePart",
        "WordprocessingPeoplePart", "GlossaryDocumentPart", "CoreFilePropertiesPart",
        "ExtendedFilePropertiesPart", "CustomFilePropertiesPart", "ThumbnailPart",
        "WorksheetCommentsPart", "WorksheetThreadedCommentsPart", "WorkbookPersonPart",
        "DigitalSignatureOriginPart", "XmlSignaturePart", "CalculationChainPart",
        "WebExTaskpanesPart", "WebExtensionPart", "LabelInfoPart"
    };

    /// <summary>Binary parts that may stay as they are.</summary>
    private static readonly HashSet<string> KeptBinaryPartTypes = new(StringComparer.Ordinal)
    {
        "FontPart", "SpreadsheetPrinterSettingsPart"
    };

    /// <summary>Free-text attributes: alt text, tooltips, field instructions, drawing names.</summary>
    private static readonly HashSet<string> FreeTextAttributes = new(StringComparer.Ordinal)
    {
        "descr", "title", "tooltip", "alt", "instr"
    };

    /// <summary>Attributes that name a person. Emptied, never matched.</summary>
    private static readonly HashSet<string> PersonAttributes = new(StringComparer.Ordinal)
    {
        "author", "initials", "userId", "lastModifiedBy"
    };

    /// <summary>Attributes verification reads in addition to the free-text ones.</summary>
    private static readonly HashSet<string> VerifiedOnlyAttributes = new(StringComparer.Ordinal)
    {
        "name", "displayName", "author", "initials", "userId", "lastModifiedBy", "comment"
    };

    public static bool Supports(string extension) =>
        extension is ".docx" or ".xlsx";

    /// <summary>
    /// The masked copy of <paramref name="file"/>, or <see cref="Outcome.UnsupportedContent"/>
    /// when the package holds something that cannot be rewritten safely.
    /// </summary>
    public static Outcome Mask(byte[] file, string extension, PiiValueMatcher matcher)
    {
        using var buffer = new MemoryStream();
        buffer.Write(file, 0, file.Length);
        buffer.Position = 0;

        bool supported;
        using (var package = Open(buffer, extension, editable: true))
        {
            supported = extension == ".docx"
                ? MaskWordDocument((WordprocessingDocument)package, matcher)
                : MaskSpreadsheet((SpreadsheetDocument)package, matcher);
        }

        return supported ? new Outcome(buffer.ToArray(), false) : Unsupported;
    }

    /// <summary>Every string of text in the package, for the scope asked.</summary>
    public static List<string> CollectText(byte[] file, string extension, TextScope scope)
    {
        using var buffer = new MemoryStream(file, writable: false);
        using var package = Open(buffer, extension, editable: false);
        var texts = new List<string>();

        HashSet<OpenXmlElement>? referencedSharedStrings = null;
        if (scope == TextScope.OutsideExtractedText && package is SpreadsheetDocument sheetDoc)
        {
            referencedSharedStrings = ReferencedSharedStrings(sheetDoc);
        }

        foreach (var part in AllParts(package))
        {
            var typeName = part.GetType().Name;
            if (scope == TextScope.OutsideExtractedText && RemovedPartTypes.Contains(typeName))
            {
                // Dropped from the masked copy whatever it says, so there is nothing to scan.
                continue;
            }

            foreach (var relationship in part.HyperlinkRelationships)
            {
                texts.Add(Uri.UnescapeDataString(relationship.Uri.OriginalString));
            }

            foreach (var relationship in part.ExternalRelationships)
            {
                texts.Add(Uri.UnescapeDataString(relationship.Uri.OriginalString));
            }

            var root = SafeRoot(part);
            if (root is null)
            {
                continue;
            }

            CollectFromRoot(part, root, scope, referencedSharedStrings, texts);
        }

        if (scope == TextScope.Everything)
        {
            var properties = package.PackageProperties;
            texts.AddRange(new[]
            {
                properties.Creator, properties.LastModifiedBy, properties.Title, properties.Subject,
                properties.Description, properties.Keywords, properties.Category, properties.ContentStatus
            }.Where(v => !string.IsNullOrWhiteSpace(v))!);
        }

        return texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
    }

    // ------------------------------------------------------------------ Word

    private static bool MaskWordDocument(WordprocessingDocument document, PiiValueMatcher matcher)
    {
        if (document.MainDocumentPart is null || !PrunePackage(document))
        {
            return false;
        }

        foreach (var part in AllParts(document))
        {
            var root = SafeRoot(part);
            if (root is null)
            {
                continue;
            }

            // Tracked deletions are text a reader does not see and the file still carries.
            RemoveAll<W.DeletedRun>(root);
            RemoveAll<W.MoveFromRun>(root);
            foreach (var orphan in root.Descendants<W.DeletedText>().ToList())
            {
                (orphan.Parent ?? orphan).Remove();
            }

            // Their part is gone; the anchors that pointed into it go too.
            RemoveAll<W.CommentRangeStart>(root);
            RemoveAll<W.CommentRangeEnd>(root);
            RemoveAll<W.CommentReference>(root);

            RewriteRoot(root, matcher);
        }

        ScrubRelationships(document, matcher);
        ClearPackageProperties(document);
        return true;
    }

    // ----------------------------------------------------------- Spreadsheet

    private static bool MaskSpreadsheet(SpreadsheetDocument document, PiiValueMatcher matcher)
    {
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook is null)
        {
            return false;
        }

        // A sheet name cannot hold a marker — "[" and "]" are not allowed in one — and formulas
        // refer to sheets by name, so renaming is not a local edit.
        var sheets = workbookPart.Workbook.Sheets?.Elements<S.Sheet>() ?? Enumerable.Empty<S.Sheet>();
        if (sheets.Any(sheet => matcher.ContainsAny(sheet.Name?.Value)))
        {
            return false;
        }

        if (!PrunePackage(document))
        {
            return false;
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<S.SharedStringItem>()
            .ToList();

        // Furigana repeats the cell text in another script; gone before anything is matched, so
        // a cell's text is what a reader sees and not that plus its reading.
        foreach (var item in sharedStrings ?? new List<S.SharedStringItem>())
        {
            RemoveAll<S.PhoneticRun>(item);
            RemoveAll<S.PhoneticProperties>(item);
        }

        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            var worksheet = worksheetPart.Worksheet;
            if (worksheet is null)
            {
                continue;
            }

            // Embedded OLE objects and form controls are drawn through the legacy VML part, which
            // is being removed along with the comment boxes it also draws.
            if (worksheet.Descendants<S.OleObjects>().Any() || worksheet.Descendants<S.Controls>().Any())
            {
                return false;
            }

            foreach (var vml in worksheetPart.VmlDrawingParts.ToList())
            {
                worksheetPart.DeletePart(vml);
            }

            RemoveAll<S.LegacyDrawing>(worksheet);
            RemoveAll<S.LegacyDrawingHeaderFooter>(worksheet);
            RemoveAll<S.HeaderFooter>(worksheet);

            foreach (var cell in worksheet.Descendants<S.Cell>())
            {
                if (cell.CellFormula is not null && matcher.ContainsAny(cell.CellFormula.Text))
                {
                    // The formula spells the value out; keep its last result (masked below).
                    cell.CellFormula.Remove();
                }
            }

            foreach (var row in worksheet.Descendants<S.Row>())
            {
                MaskRow(row, sharedStrings, matcher);
            }
        }

        foreach (var part in AllParts(document))
        {
            var root = SafeRoot(part);
            if (root is null)
            {
                continue;
            }

            // Furigana and its settings repeat the cell text in another script.
            RemoveAll<S.PhoneticRun>(root);
            RemoveAll<S.PhoneticProperties>(root);

            RewriteRoot(root, matcher);
        }

        ScrubRelationships(document, matcher);
        ClearPackageProperties(document);
        return true;
    }

    // ---------------------------------------------------------------- shared

    private static OpenXmlPackage Open(Stream stream, string extension, bool editable) => extension switch
    {
        ".docx" => WordprocessingDocument.Open(stream, editable),
        ".xlsx" => SpreadsheetDocument.Open(stream, editable),
        _ => throw new NotSupportedException($"No OpenXML masker for '{extension}'.")
    };

    /// <summary>
    /// Deletes what a masked copy must not carry and blanks what cannot be read. False when the
    /// package holds a part this refuses to handle.
    /// </summary>
    private static bool PrunePackage(OpenXmlPackage package)
    {
        var containers = new List<OpenXmlPartContainer> { package };
        containers.AddRange(AllParts(package));

        if (containers.OfType<OpenXmlPart>().Any(p => RefusedPartTypes.Contains(p.GetType().Name)))
        {
            return false;
        }

        // One at a time, re-reading the tree after each: deleting a part destroys the parts
        // under it, and a destroyed part cannot be asked for its children.
        while (true)
        {
            var doomed = FindRemovablePart(package);
            if (doomed is null)
            {
                break;
            }

            doomed.Value.Container.DeletePart(doomed.Value.Part);
        }

        foreach (var part in AllParts(package))
        {
            var typeName = part.GetType().Name;
            if (part is ImagePart)
            {
                using var blank = new MemoryStream(BlankImage, writable: false);
                part.FeedData(blank);
            }
            else if (typeName.StartsWith("Embedded", StringComparison.Ordinal))
            {
                using var empty = new MemoryStream(Array.Empty<byte>(), writable: false);
                part.FeedData(empty);
            }
            else if (SafeRoot(part) is null && !KeptBinaryPartTypes.Contains(typeName))
            {
                // A binary part of a kind nobody has looked at.
                return false;
            }
        }

        return true;
    }

    private static (OpenXmlPartContainer Container, OpenXmlPart Part)? FindRemovablePart(OpenXmlPackage package)
    {
        var containers = new List<OpenXmlPartContainer> { package };
        containers.AddRange(AllParts(package));

        foreach (var container in containers)
        {
            foreach (var pair in container.Parts)
            {
                if (RemovedPartTypes.Contains(pair.OpenXmlPart.GetType().Name))
                {
                    return (container, pair.OpenXmlPart);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Hides values in one worksheet row, including a value that runs from one cell into the next.
    /// </summary>
    /// <remarks>
    /// The text the scan read is each row's cells joined by a single space — that is how
    /// DocumentTextExtractor builds it — so a first name in one cell and a surname in the next
    /// come back from the scan as ONE hidden value. Matching per cell would find neither half.
    /// So the row is matched the way it was read, and each cell loses the part of a match that
    /// falls inside it.
    ///
    /// A shared string is rewritten in the string table itself rather than detached from it:
    /// leaving the old entry behind would leave the value in the package with no cell showing it.
    /// </remarks>
    private static void MaskRow(S.Row row, List<S.SharedStringItem>? sharedStrings, PiiValueMatcher matcher)
    {
        var cells = new List<(S.Cell Cell, S.SharedStringItem? Shared, string Text)>();
        foreach (var cell in row.Elements<S.Cell>())
        {
            var value = cell.CellValue?.Text ?? cell.InnerText;
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            S.SharedStringItem? shared = null;
            if (cell.DataType?.Value == S.CellValues.SharedString
                && sharedStrings is not null
                && int.TryParse(value, out var index)
                && index >= 0
                && index < sharedStrings.Count)
            {
                shared = sharedStrings[index];
                value = shared.InnerText;
            }

            cells.Add((cell, shared, value));
        }

        if (cells.Count == 0)
        {
            return;
        }

        var joined = string.Join(" ", cells.Select(c => c.Text));
        var matches = matcher.FindMatches(joined);
        if (matches.Count == 0)
        {
            return;
        }

        var offset = 0;
        foreach (var (cell, shared, text) in cells)
        {
            var cellStart = offset;
            var cellEnd = offset + text.Length;
            offset = cellEnd + 1;

            var builder = new System.Text.StringBuilder();
            var position = cellStart;
            var touched = false;
            foreach (var match in matches)
            {
                var from = Math.Max(match.Start, cellStart);
                var to = Math.Min(match.Start + match.Length, cellEnd);
                if (from >= to)
                {
                    continue;
                }

                builder.Append(joined, position, from - position);
                builder.Append(match.Placeholder);
                position = to;
                touched = true;
            }

            if (!touched)
            {
                continue;
            }

            builder.Append(joined, position, cellEnd - position);
            var masked = builder.ToString();

            if (shared is not null)
            {
                shared.RemoveAllChildren();
                shared.Append(new S.Text(masked) { Space = SpaceProcessingModeValues.Preserve });
            }
            else if (cell.InlineString is not null)
            {
                cell.InlineString.RemoveAllChildren();
                cell.InlineString.Append(new S.Text(masked) { Space = SpaceProcessingModeValues.Preserve });
            }
            else
            {
                cell.CellFormula?.Remove();
                cell.CellValue = new S.CellValue(masked);
                cell.DataType = S.CellValues.String;
            }
        }
    }

    private static void RewriteRoot(OpenXmlPartRootElement root, PiiValueMatcher matcher)
    {
        var groups = new Dictionary<OpenXmlElement, List<OpenXmlLeafTextElement>>();
        var order = new List<OpenXmlElement>();

        foreach (var leaf in root.Descendants<OpenXmlLeafTextElement>().ToList())
        {
            if (IsRunText(leaf))
            {
                var container = leaf.Ancestors().FirstOrDefault(IsTextContainer) ?? leaf;
                if (!groups.TryGetValue(container, out var group))
                {
                    group = new List<OpenXmlLeafTextElement>();
                    groups[container] = group;
                    order.Add(container);
                }

                group.Add(leaf);
            }
            else if (IsStandaloneText(leaf) && matcher.ContainsAny(leaf.Text))
            {
                leaf.Text = matcher.Replace(leaf.Text);
            }
        }

        foreach (var container in order)
        {
            ReplaceAcross(groups[container], matcher);
        }

        foreach (var element in root.Descendants().Prepend(root).ToList())
        {
            if (!element.HasAttributes)
            {
                continue;
            }

            foreach (var attribute in element.GetAttributes().ToList())
            {
                if (PersonAttributes.Contains(attribute.LocalName))
                {
                    if (!string.IsNullOrEmpty(attribute.Value))
                    {
                        element.SetAttribute(new OpenXmlAttribute(
                            attribute.Prefix, attribute.LocalName, attribute.NamespaceUri, string.Empty));
                    }
                }
                else if (FreeTextAttributes.Contains(attribute.LocalName) && matcher.ContainsAny(attribute.Value))
                {
                    element.SetAttribute(new OpenXmlAttribute(
                        attribute.Prefix, attribute.LocalName, attribute.NamespaceUri, matcher.Replace(attribute.Value)));
                }
            }
        }
    }

    /// <summary>
    /// Replaces matches found on the JOINED text of a paragraph's runs. A name typed in one go
    /// and then half-bolded lives in two runs; neither holds it, the paragraph does.
    /// </summary>
    private static void ReplaceAcross(List<OpenXmlLeafTextElement> elements, PiiValueMatcher matcher)
    {
        var texts = elements.Select(e => e.Text ?? string.Empty).ToArray();
        var joined = string.Concat(texts);
        var matches = matcher.FindMatches(joined);
        if (matches.Count == 0)
        {
            return;
        }

        var starts = new int[texts.Length];
        for (int i = 0, offset = 0; i < texts.Length; i++)
        {
            starts[i] = offset;
            offset += texts[i].Length;
        }

        var original = (string[])texts.Clone();
        var touched = new bool[texts.Length];

        // Last match first: every edit then lands at or after the edits still to come, so the
        // offsets computed on the untouched text stay valid.
        for (var m = matches.Count - 1; m >= 0; m--)
        {
            var match = matches[m];
            var matchEnd = match.Start + match.Length;
            var first = IndexOfElementAt(starts, original, match.Start);
            var last = IndexOfElementAt(starts, original, matchEnd - 1);

            var localStart = match.Start - starts[first];
            if (first == last)
            {
                texts[first] = texts[first].Remove(localStart, match.Length).Insert(localStart, match.Placeholder);
            }
            else
            {
                // `texts[last]` may already have lost its tail to a later match; what is cut here
                // is its head, which no later match touched.
                var cut = matchEnd - starts[last];
                texts[last] = texts[last][cut..];
                for (var i = first + 1; i < last; i++)
                {
                    texts[i] = string.Empty;
                    touched[i] = true;
                }

                texts[first] = texts[first][..localStart] + match.Placeholder;
                touched[last] = true;
            }

            touched[first] = true;
        }

        for (var i = 0; i < elements.Count; i++)
        {
            if (!touched[i])
            {
                continue;
            }

            elements[i].Text = texts[i];
            if (elements[i] is W.Text wordText)
            {
                wordText.Space = SpaceProcessingModeValues.Preserve;
            }
            else if (elements[i] is S.Text sheetText)
            {
                sheetText.Space = SpaceProcessingModeValues.Preserve;
            }
        }
    }

    private static int IndexOfElementAt(int[] starts, string[] texts, int position)
    {
        for (var i = starts.Length - 1; i >= 0; i--)
        {
            if (starts[i] <= position && texts[i].Length > 0)
            {
                return i;
            }
        }

        return 0;
    }

    private static void CollectFromRoot(
        OpenXmlPart part,
        OpenXmlPartRootElement root,
        TextScope scope,
        HashSet<OpenXmlElement>? referencedSharedStrings,
        List<string> texts)
    {
        var outsideOnly = scope == TextScope.OutsideExtractedText;
        var groups = new Dictionary<OpenXmlElement, List<string>>();
        var order = new List<OpenXmlElement>();

        foreach (var leaf in root.Descendants().Where(e => !e.HasChildren))
        {
            var text = leaf.InnerText;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            // A shared-string cell holds an index into the string table, not text.
            if (leaf is S.CellValue && leaf.Parent is S.Cell { DataType: { } type } && type.Value == S.CellValues.SharedString)
            {
                continue;
            }

            if (outsideOnly && IsCoveredByExtractor(part, leaf, referencedSharedStrings))
            {
                continue;
            }

            if (leaf is OpenXmlLeafTextElement textLeaf && IsRunText(textLeaf))
            {
                var container = leaf.Ancestors().FirstOrDefault(IsTextContainer) ?? leaf;
                if (!groups.TryGetValue(container, out var group))
                {
                    group = new List<string>();
                    groups[container] = group;
                    order.Add(container);
                }

                group.Add(text);
            }
            else if (!outsideOnly || IsWorthScanning(text))
            {
                texts.Add(text);
            }
        }

        foreach (var container in order)
        {
            texts.Add(string.Concat(groups[container]));
        }

        foreach (var element in root.Descendants().Prepend(root))
        {
            if (!element.HasAttributes)
            {
                continue;
            }

            foreach (var attribute in element.GetAttributes())
            {
                if (string.IsNullOrWhiteSpace(attribute.Value))
                {
                    continue;
                }

                if (FreeTextAttributes.Contains(attribute.LocalName)
                    || (!outsideOnly && VerifiedOnlyAttributes.Contains(attribute.LocalName))
                    || (outsideOnly && element is S.Sheet && attribute.LocalName == "name"))
                {
                    texts.Add(attribute.Value!);
                }
            }
        }
    }

    /// <summary>
    /// Does DocumentTextExtractor already return this text? Word: everything inside a body
    /// paragraph. Excel: every cell, and every shared string a cell points at.
    /// </summary>
    private static bool IsCoveredByExtractor(OpenXmlPart part, OpenXmlElement leaf, HashSet<OpenXmlElement>? referencedSharedStrings)
    {
        if (part is MainDocumentPart)
        {
            return leaf.Ancestors<W.Paragraph>().Any();
        }

        if (part is WorksheetPart)
        {
            return leaf.Ancestors<S.Cell>().Any();
        }

        if (part is SharedStringTablePart)
        {
            var item = leaf.Ancestors<S.SharedStringItem>().FirstOrDefault();
            return item is not null && referencedSharedStrings is not null && referencedSharedStrings.Contains(item);
        }

        return false;
    }

    private static HashSet<OpenXmlElement> ReferencedSharedStrings(SpreadsheetDocument document)
    {
        var referenced = new HashSet<OpenXmlElement>();
        var workbookPart = document.WorkbookPart;
        var items = workbookPart?.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().ToList();
        if (workbookPart is null || items is null)
        {
            return referenced;
        }

        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            foreach (var cell in worksheetPart.Worksheet?.Descendants<S.Cell>() ?? Enumerable.Empty<S.Cell>())
            {
                if (cell.DataType?.Value == S.CellValues.SharedString
                    && int.TryParse(cell.CellValue?.Text, out var index)
                    && index >= 0
                    && index < items.Count)
                {
                    referenced.Add(items[index]);
                }
            }
        }

        return referenced;
    }

    /// <summary>Layout numbers and one-letter flags are not worth a model call.</summary>
    private static bool IsWorthScanning(string text) =>
        text.Any(char.IsLetter) || text.Count(char.IsDigit) >= 5;

    private static bool IsRunText(OpenXmlLeafTextElement leaf) =>
        // Field codes ride along with the paragraph text: Word splits a long instruction such as
        // HYPERLINK "mailto:…" across runs exactly the way it splits visible text.
        leaf is W.Text or W.FieldCode or A.Text or S.Text or M.Text;

    private static bool IsTextContainer(OpenXmlElement element) =>
        element is W.Paragraph or A.Paragraph or S.SharedStringItem or S.InlineString;

    private static bool IsStandaloneText(OpenXmlLeafTextElement leaf) =>
        leaf is C.NumericValue && leaf.Parent is C.StringPoint;

    private static void ScrubRelationships(OpenXmlPackage package, PiiValueMatcher matcher)
    {
        var blank = new Uri("about:blank");

        foreach (var part in AllParts(package))
        {
            foreach (var link in part.HyperlinkRelationships.ToList())
            {
                var target = Uri.UnescapeDataString(link.Uri.OriginalString);
                if (IsWebAddress(target) && !matcher.ContainsAny(target))
                {
                    continue;
                }

                // mailto:, tel:, file paths, or a web address that spells a hidden value.
                var id = link.Id;
                part.DeleteReferenceRelationship(link);
                part.AddHyperlinkRelationship(blank, true, id);
            }

            foreach (var external in part.ExternalRelationships.ToList())
            {
                var target = Uri.UnescapeDataString(external.Uri.OriginalString);
                if (IsWebAddress(target) && !matcher.ContainsAny(target))
                {
                    continue;
                }

                // Usually a template or linked file path, which names the machine and its user.
                var id = external.Id;
                var type = external.RelationshipType;
                part.DeleteReferenceRelationship(external);
                part.AddExternalRelationship(type, blank, id);
            }
        }
    }

    private static bool IsWebAddress(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("#", StringComparison.Ordinal);

    private static void ClearPackageProperties(OpenXmlPackage package)
    {
        var properties = package.PackageProperties;
        properties.Creator = null;
        properties.LastModifiedBy = null;
        properties.Title = null;
        properties.Subject = null;
        properties.Description = null;
        properties.Keywords = null;
        properties.Category = null;
        properties.ContentStatus = null;
    }

    private static void RemoveAll<T>(OpenXmlElement root) where T : OpenXmlElement
    {
        foreach (var element in root.Descendants<T>().ToList())
        {
            element.Remove();
        }
    }

    private static OpenXmlPartRootElement? SafeRoot(OpenXmlPart part)
    {
        if (part is ImagePart || part is CustomXmlPart)
        {
            return null;
        }

        try
        {
            return part.RootElement;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException or InvalidDataException)
        {
            return null;
        }
    }

    private static List<OpenXmlPart> AllParts(OpenXmlPartContainer container)
    {
        var seen = new HashSet<OpenXmlPart>();
        var ordered = new List<OpenXmlPart>();
        Visit(container);
        return ordered;

        void Visit(OpenXmlPartContainer current)
        {
            foreach (var pair in current.Parts)
            {
                if (seen.Add(pair.OpenXmlPart))
                {
                    ordered.Add(pair.OpenXmlPart);
                    Visit(pair.OpenXmlPart);
                }
            }
        }
    }
}
