using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Masking;

/// <summary>
/// DOCX → PDF through Gotenberg, the LibreOffice sidecar the minutes export already uses.
/// </summary>
/// <remarks>
/// Unconfigured is a supported state: with no <c>Gotenberg:Url</c> / <c>GOTENBERG_URL</c> a PDF
/// simply gets no masked copy, and the document page says so. Every failure is null for the same
/// reason — a masked copy is something a document may or may not have, and "the converter was
/// down" must end in "it does not have one", not in a failed ingestion.
///
/// The bytes sent are the masked text already laid out as a Word file; the original PDF never
/// leaves this service.
/// </remarks>
public sealed class GotenbergMaskedPdfRenderer : IMaskedPdfRenderer
{
    public const string HttpClientName = nameof(GotenbergMaskedPdfRenderer);

    private const string ConvertPath = "forms/libreoffice/convert";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GotenbergMaskedPdfRenderer> _logger;
    private readonly string? _baseUrl;

    public GotenbergMaskedPdfRenderer(
        IHttpClientFactory httpClientFactory,
        ILogger<GotenbergMaskedPdfRenderer> logger,
        string? baseUrl)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
    }

    public bool IsConfigured => _baseUrl != null;

    public async Task<byte[]?> RenderFromDocxAsync(byte[] docx, CancellationToken ct = default)
    {
        if (_baseUrl == null)
        {
            return null;
        }

        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(docx);
            file.Headers.ContentType = new MediaTypeHeaderValue(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

            // Gotenberg picks its converter from the extension. The name is fixed and ASCII: it
            // travels in a MIME header, and the document's own name is nobody's business there.
            content.Add(file, "files", "masked.docx");

            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync($"{_baseUrl}/{ConvertPath}", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gotenberg refused a masked-copy conversion: {Status}", (int)response.StatusCode);
                return null;
            }

            var pdf = await response.Content.ReadAsByteArrayAsync(ct);
            return pdf.Length == 0 ? null : pdf;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gotenberg did not answer a masked-copy conversion.");
            return null;
        }
    }
}
