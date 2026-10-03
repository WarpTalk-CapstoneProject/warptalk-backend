using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// Turns a report the admin portal has already built as a Word file into a PDF (WT-892).
///
/// WHY THE PORTAL SENDS THE .docx INSTEAD OF THIS SERVICE BUILDING THE REPORT
///     The Insights report is laid out in the browser from the same figures the dashboard shows,
///     charts included. A server-side builder would be a second layout of the same document — the
///     drift the minutes export avoids by converting its own .docx (see <see cref="IDocumentPdfConverter"/>).
///     Here the portal's file is the original and the PDF is a rendering of it, so the two cannot disagree.
///
/// WHY THIS SERVICE
///     translation-room already runs the Gotenberg client and has the network path to it. Putting the
///     endpoint in billing would have meant a third copy of the client plus its environment variable
///     and network policy. The content has nothing to do with meetings; the converter does not look at it.
///
/// WHAT IT WILL NOT DO
///     It stores nothing and reads nothing from any database: bytes in, bytes out. It only accepts a
///     real .docx (a zip), capped at <see cref="MaxDocxBytes"/>, from a caller who may read billing
///     insights — the permission the report's figures come from.
/// </summary>
[ApiController]
[Route("api/v1/admin/reports")]
[RequirePermission(AdminPermissions.BillingRead)]
public class AdminReportsController : ControllerBase
{
    /// <summary>A four-page report with charts is a few hundred KB; 10 MB is far beyond any real one.</summary>
    public const int MaxDocxBytes = 10 * 1024 * 1024;

    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public const string PdfUnavailableMessage =
        "PDF conversion is not available on this deployment. The Word report can still be exported.";

    private readonly IDocumentPdfConverter _pdfConverter;

    public AdminReportsController(IDocumentPdfConverter pdfConverter)
    {
        _pdfConverter = pdfConverter;
    }

    /// <summary>
    /// <c>POST ~/api/v1/admin/reports/pdf</c> with a .docx as the request body; answers with the PDF.
    /// 400 when the body is empty, not a .docx, or over the cap; 503 when this deployment has no converter
    /// or the conversion failed (the Word file the caller holds is still good).
    /// </summary>
    [HttpPost("pdf")]
    [RequestSizeLimit(MaxDocxBytes)]
    public async Task<IActionResult> DocxToPdf(CancellationToken ct)
    {
        if (!_pdfConverter.IsConfigured)
        {
            return StatusCode(503, new ApiErrorResponse(PdfUnavailableMessage, ErrorCodes.ServiceUnavailable));
        }

        if (Request.ContentLength is > MaxDocxBytes)
        {
            return BadRequest(new ApiErrorResponse("The report is too large to convert.", ErrorCodes.ValidationError));
        }

        var docx = await ReadBodyAsync(ct);
        if (docx is null)
        {
            return BadRequest(new ApiErrorResponse("The report is too large to convert.", ErrorCodes.ValidationError));
        }

        if (!LooksLikeDocx(docx))
        {
            return BadRequest(new ApiErrorResponse("The body must be a Word (.docx) file.", ErrorCodes.ValidationError));
        }

        var pdf = await _pdfConverter.ToPdfAsync(docx, "report.docx", ct);
        if (pdf is null)
        {
            return StatusCode(503, new ApiErrorResponse(PdfUnavailableMessage, ErrorCodes.ServiceUnavailable));
        }

        // The name the person sees is chosen by the browser's save step; this one is a fallback.
        return File(pdf, "application/pdf", "report.pdf");
    }

    /// <summary>The request body, or null when it is longer than <see cref="MaxDocxBytes"/> (a chunked body has no length to check up front).</summary>
    private async Task<byte[]?> ReadBodyAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)) > 0)
        {
            if (buffer.Length + read > MaxDocxBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>A .docx is a zip: it starts "PK\x03\x04". Anything else is not worth handing to an office engine.</summary>
    public static bool LooksLikeDocx(byte[] bytes)
        => bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;
}
