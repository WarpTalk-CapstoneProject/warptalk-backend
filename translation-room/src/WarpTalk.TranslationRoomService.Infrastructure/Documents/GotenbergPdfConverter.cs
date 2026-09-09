using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Documents;

/// <summary>
/// DOCX → PDF through Gotenberg, a container that wraps LibreOffice behind an HTTP call.
///
/// WHY A SIDECAR AND NOT LIBREOFFICE IN THIS IMAGE
///     soffice is a desktop program: it wants a writable HOME profile, it does not like being run
///     concurrently, and a hung conversion leaves a process behind. Inside the API container that
///     is the API's memory, the API's file descriptors and the API's restart. As its own service
///     it has its own limits and its own restart policy, and the worst a bad document can do is
///     make one HTTP call fail.
///
/// WHY EVERY FAILURE IS null AND NOT AN EXCEPTION
///     PDF is a convenience on top of a document that already downloads. A conversion that times
///     out must degrade to "PDF is unavailable, the Word file still works", never to a 500 on a
///     page somebody opened to read minutes.
/// </summary>
public class GotenbergPdfConverter : IDocumentPdfConverter
{
    /// <summary>Gotenberg's LibreOffice route. Stable across its 7.x and 8.x lines.</summary>
    private const string ConvertPath = "forms/libreoffice/convert";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GotenbergPdfConverter> _logger;
    private readonly string? _baseUrl;

    public GotenbergPdfConverter(
        IHttpClientFactory httpClientFactory,
        ILogger<GotenbergPdfConverter> logger,
        string? baseUrl)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
    }

    public bool IsConfigured => _baseUrl != null;

    public async Task<byte[]?> ToPdfAsync(byte[] docx, string fileName, CancellationToken ct = default)
    {
        if (_baseUrl == null) return null;

        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(docx);
            file.Headers.ContentType = new MediaTypeHeaderValue(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

            // Gotenberg picks its converter from the extension, so the extension matters even
            // though nobody sees this name: the name the USER gets is set by Content-Disposition
            // on the way out. Deliberately ASCII — the upload name travels in a MIME header, and a
            // Vietnamese meeting title in there is the one place this could produce an unsendable
            // request.
            content.Add(file, "files", "minutes.docx");

            using var client = _httpClientFactory.CreateClient(nameof(GotenbergPdfConverter));
            using var response = await client.PostAsync($"{_baseUrl}/{ConvertPath}", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Gotenberg refused a conversion: {Status}", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away. Not a conversion failure worth logging as one.
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "PDF conversion failed; the caller falls back to .docx");
            return null;
        }
    }
}
