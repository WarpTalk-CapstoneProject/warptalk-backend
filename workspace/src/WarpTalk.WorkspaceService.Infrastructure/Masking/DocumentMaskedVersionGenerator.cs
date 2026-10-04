using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Masking;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;

namespace WarpTalk.WorkspaceService.Infrastructure.Masking;

/// <summary>
/// Turns a finished security scan into a masked copy of the file, in the format it was uploaded
/// in, and stores it next to the original — or stores nothing.
/// </summary>
/// <remarks>
/// WHAT KEEPS ITS LAYOUT
///   .docx, .xlsx   rewritten in place: tables, styles, headers and page layout survive. Pictures
///                  and embedded objects are blanked, comments and document properties removed.
///   .md, .txt      the same text with the values replaced.
///   .pdf           NOT preserved. The masked text is laid out as a new PDF, page for page. See
///                  <see cref="IMaskedPdfRenderer"/> for why this is not redaction in place.
///   images         no masked copy. There is no text to rewrite, and a picture of an ID card is
///                  exactly what must not be served.
///
/// THE ORDER, AND WHY IT IS FAIL-CLOSED
///   1. Locate the hidden values by aligning the scanned text with the masked text the worker
///      returned. Cannot be located → no copy.
///   2. Office files: put the text the first scan never saw (headers, footers, footnotes, sheet
///      names, chart text) through the scan as well. Scan fails → no copy.
///   3. Rewrite.
///   4. VERIFY the result, not the intent: read the masked file back with the ordinary extractor
///      and with a walk over every part, and require that none of the values is still there and
///      that the worker's own structured patterns (email, phone, card, citizen id) find nothing.
///      Anything left → the file is thrown away.
///   5. Store. Only now does a masked copy exist.
///
/// Every exit that is not step 5 also deletes whatever copy an earlier scan left, so a document
/// never keeps a masked file that the latest scan would not have produced.
///
/// NOTHING HERE LOGS A VALUE. The values are personal data; log lines carry counts and statuses.
/// </remarks>
public sealed class DocumentMaskedVersionGenerator : IDocumentMaskedVersionGenerator
{
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt"
    };

    private readonly IWorkspaceDocumentStorage _storage;
    private readonly IDocumentTextExtractor _textExtractor;
    private readonly IDocumentSecurityScanner _securityScanner;
    private readonly IMaskedPdfRenderer _pdfRenderer;
    private readonly ILogger<DocumentMaskedVersionGenerator> _logger;

    public DocumentMaskedVersionGenerator(
        IWorkspaceDocumentStorage storage,
        IDocumentTextExtractor textExtractor,
        IDocumentSecurityScanner securityScanner,
        IMaskedPdfRenderer pdfRenderer,
        ILogger<DocumentMaskedVersionGenerator> logger)
    {
        _storage = storage;
        _textExtractor = textExtractor;
        _securityScanner = securityScanner;
        _pdfRenderer = pdfRenderer;
        _logger = logger;
    }

    public async Task<string> GenerateAsync(
        WorkspaceDocument document,
        byte[] originalFile,
        ExtractedDocumentContent extracted,
        DocumentSecurityScanResult scanResult,
        CancellationToken ct = default)
    {
        string status;
        byte[]? masked = null;

        try
        {
            (status, masked) = await BuildAsync(document, originalFile, extracted, scanResult, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The exception TYPE only. A message from the OpenXML reader can quote the text it
            // choked on, and that text may be the very thing being hidden.
            _logger.LogError(
                "Could not build a masked copy of document {DocumentId}: {ExceptionType}.",
                document.Id,
                ex.GetType().Name);
            status = WorkspaceDocumentMaskedVersionStatuses.Error;
        }

        try
        {
            if (masked is not null && status == WorkspaceDocumentMaskedVersionStatuses.Available)
            {
                using var stream = new MemoryStream(masked, writable: false);
                await _storage.SaveMaskedFileAsync(document, stream, ct);
            }
            else
            {
                await _storage.DeleteMaskedFileAsync(document, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not {Action} the masked copy of document {DocumentId}.",
                status == WorkspaceDocumentMaskedVersionStatuses.Available ? "store" : "remove",
                document.Id);
            status = WorkspaceDocumentMaskedVersionStatuses.Error;
        }

        _logger.LogInformation(
            "Masked copy of document {DocumentId} ({Extension}): {Status}.",
            document.Id,
            document.FileExtension,
            status);
        return status;
    }

    public Task DiscardAsync(WorkspaceDocument document, CancellationToken ct = default)
        => _storage.DeleteMaskedFileAsync(document, ct);

    private async Task<(string Status, byte[]? Masked)> BuildAsync(
        WorkspaceDocument document,
        byte[] originalFile,
        ExtractedDocumentContent extracted,
        DocumentSecurityScanResult scanResult,
        CancellationToken ct)
    {
        if (scanResult.DlpDetected)
        {
            return (WorkspaceDocumentMaskedVersionStatuses.DlpBlocked, null);
        }

        if (!scanResult.PiiDetected)
        {
            return (WorkspaceDocumentMaskedVersionStatuses.NoPiiFound, null);
        }

        var originalText = extracted.FullText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(scanResult.MaskedContent)
            || string.Equals(scanResult.MaskedContent.Trim(), originalText.Trim(), StringComparison.Ordinal))
        {
            return (WorkspaceDocumentMaskedVersionStatuses.MaskedTextUnavailable, null);
        }

        var extension = WorkspaceDocumentHelper.NormalizeExtension(document.FileExtension);
        var isOffice = OpenXmlPiiMasker.Supports(extension);
        var isPdf = extension == ".pdf";
        if (!isOffice && !isPdf && !PlainTextExtensions.Contains(extension))
        {
            return (WorkspaceDocumentMaskedVersionStatuses.UnsupportedFormat, null);
        }

        var replacements = PiiMaskAlignment.DeriveReplacements(originalText, scanResult.MaskedContent);
        if (replacements is null || replacements.Count == 0)
        {
            return (WorkspaceDocumentMaskedVersionStatuses.AlignmentFailed, null);
        }

        var all = new List<PiiReplacement>(replacements);

        if (isOffice)
        {
            // The part of the file the first scan never read.
            var outside = string.Join(
                "\n",
                OpenXmlPiiMasker.CollectText(originalFile, extension, OpenXmlPiiMasker.TextScope.OutsideExtractedText));
            if (!string.IsNullOrWhiteSpace(outside))
            {
                var outsideScan = await _securityScanner.ScanAsync(outside, true, false, null, ct);
                if (outsideScan.PiiDetected)
                {
                    var outsideReplacements = PiiMaskAlignment.DeriveReplacements(outside, outsideScan.MaskedContent);
                    if (outsideReplacements is null)
                    {
                        return (WorkspaceDocumentMaskedVersionStatuses.AlignmentFailed, null);
                    }

                    all.AddRange(outsideReplacements);
                }
            }
        }

        var matcher = new PiiValueMatcher(all);

        byte[]? masked;
        if (isOffice)
        {
            var outcome = OpenXmlPiiMasker.Mask(originalFile, extension, matcher);
            if (outcome.UnsupportedContent || outcome.Content is null)
            {
                return (WorkspaceDocumentMaskedVersionStatuses.UnsupportedContent, null);
            }

            masked = outcome.Content;
            if (!await VerifyAsync(masked, extension, matcher, ct))
            {
                return (WorkspaceDocumentMaskedVersionStatuses.VerificationFailed, null);
            }
        }
        else if (isPdf)
        {
            var pages = extracted.Pages.Count > 0
                ? extracted.Pages.Select(p => p.Text ?? string.Empty).ToList()
                : new List<string> { originalText };
            if (pages.All(string.IsNullOrWhiteSpace))
            {
                // A scan of a paper page: nothing to rewrite and nothing safe to show.
                return (WorkspaceDocumentMaskedVersionStatuses.UnsupportedContent, null);
            }

            var layout = BuildWordDocument(pages.Select(matcher.Replace).ToList());

            // Checked BEFORE rendering, on the Word file the PDF is rendered from. The rendered
            // PDF embeds subset fonts with private encodings, which this service's PDF reader
            // cannot turn back into text — checking that would be checking nothing.
            if (!await VerifyAsync(layout, ".docx", matcher, ct))
            {
                return (WorkspaceDocumentMaskedVersionStatuses.VerificationFailed, null);
            }

            if (!_pdfRenderer.IsConfigured)
            {
                return (WorkspaceDocumentMaskedVersionStatuses.ConverterUnavailable, null);
            }

            masked = await _pdfRenderer.RenderFromDocxAsync(layout, ct);
            if (masked is null)
            {
                return (WorkspaceDocumentMaskedVersionStatuses.ConverterUnavailable, null);
            }
        }
        else
        {
            masked = Encoding.UTF8.GetBytes(matcher.Replace(TextEncodingDetector.Decode(originalFile)));
            if (!await VerifyAsync(masked, extension, matcher, ct))
            {
                return (WorkspaceDocumentMaskedVersionStatuses.VerificationFailed, null);
            }
        }

        return (WorkspaceDocumentMaskedVersionStatuses.Available, masked);
    }

    /// <summary>
    /// Reads the masked file back and requires that it shows nothing it was meant to hide.
    /// </summary>
    private async Task<bool> VerifyAsync(byte[] masked, string extension, PiiValueMatcher matcher, CancellationToken ct)
    {
        ExtractedDocumentContent reread;
        using (var stream = new MemoryStream(masked, writable: false))
        {
            reread = await _textExtractor.ExtractTextAsync(stream, extension, ct);
        }

        if (!IsClean(reread.FullText, matcher))
        {
            return false;
        }

        if (OpenXmlPiiMasker.Supports(extension))
        {
            // The extractor reads the body. This reads everything else in the package too.
            foreach (var text in OpenXmlPiiMasker.CollectText(masked, extension, OpenXmlPiiMasker.TextScope.Everything))
            {
                if (!IsClean(text, matcher))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsClean(string? text, PiiValueMatcher matcher) =>
        !matcher.ContainsAny(text) && PiiRegexScanner.CountFindings(text) == 0;

    /// <summary>The masked text as a plain Word document: one paragraph per line, one page per source page.</summary>
    private static byte[] BuildWordDocument(IReadOnlyList<string> pages)
    {
        using var buffer = new MemoryStream();
        using (var word = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
        {
            var body = new Body();
            for (var i = 0; i < pages.Count; i++)
            {
                var lines = pages[i].Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                foreach (var line in lines)
                {
                    body.Append(new Paragraph(new Run(new Text(Sanitize(line)) { Space = SpaceProcessingModeValues.Preserve })));
                }

                if (i < pages.Count - 1)
                {
                    body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                }
            }

            var main = word.AddMainDocumentPart();
            main.Document = new Document(body);
        }

        return buffer.ToArray();
    }

    /// <summary>Drops characters XML 1.0 cannot carry; a PDF content stream is full of them.</summary>
    private static string Sanitize(string line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c == '\t' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF && !char.IsSurrogate(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
