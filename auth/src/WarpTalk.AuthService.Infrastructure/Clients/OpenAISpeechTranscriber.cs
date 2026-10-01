using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Infrastructure.Clients;

/// <summary>
/// WT-888 — the voice-profile recording check, transcribed by the same provider the meeting
/// pipeline uses (OpenAI; warptalk-ai <c>STTSettings.provider = "openai"</c>), through its
/// file transcription endpoint — the one <c>retranscribe_worker.BatchTranscriber</c> calls.
///
/// Called directly rather than through the AI workers: this is one short clip somebody is waiting
/// on, synchronously, and there is no meeting, stream or worker state involved.
///
/// CONFIGURATION
///     <c>OpenAI:ApiKey</c> — the same key, under the same name, the meeting service reads. Absent
///     means every check answers ServiceUnavailable: a voice profile cannot be made, and the page
///     says the recording could not be checked. Failing closed is the point of the feature.
///     <c>OpenAI:TranscriptionModel</c> — default <c>gpt-transcribe</c>, the accuracy-first family
///     warptalk-ai benchmarks as its file-transcription model.
///
/// NO PROMPT, DELIBERATELY. The endpoint accepts a decoding prompt, and passing the challenge
/// phrase as one would bias the model into hearing it — the opposite of checking for it.
/// </summary>
public sealed class OpenAISpeechTranscriber : ISpeechTranscriber
{
    private const string TranscriptionsPath = "audio/transcriptions";
    private const string DefaultModel = "gpt-transcribe";

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAISpeechTranscriber> _logger;
    private readonly string? _apiKey;
    private readonly string _model;

    public OpenAISpeechTranscriber(
        HttpClient httpClient, IConfiguration configuration, ILogger<OpenAISpeechTranscriber> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["OpenAI:ApiKey"];
        var model = configuration["OpenAI:TranscriptionModel"];
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _httpClient.BaseAddress ??= new Uri("https://api.openai.com/v1/");
    }

    public async Task<Result<string>> TranscribeAsync(
        byte[] audio,
        string fileName,
        string contentType,
        string language,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogError("OpenAI:ApiKey is not configured; voice profile recordings cannot be checked.");
            return Unavailable();
        }

        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var parsed)
                ? parsed
                : new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "recording.webm" : fileName);
            form.Add(new StringContent(_model), "model");
            form.Add(new StringContent("json"), "response_format");
            if (!string.IsNullOrWhiteSpace(language))
            {
                form.Add(new StringContent(language), "language");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsPath) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // The body is the provider's error envelope — never the key, never the audio.
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "OpenAI transcription for a voice profile recording failed. Status: {StatusCode}, Body: {Body}",
                    (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
                return Unavailable();
            }

            var payload = await response.Content.ReadFromJsonAsync<TranscriptionResponse>(cancellationToken: ct);
            return Result.Success(payload?.Text?.Trim() ?? string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI transcription for a voice profile recording failed.");
            return Unavailable();
        }
    }

    private static Result<string> Unavailable() =>
        Result.Failure<string>("Speech-to-text is unavailable.", ErrorCodes.ServiceUnavailable);

    private sealed record TranscriptionResponse([property: JsonPropertyName("text")] string? Text);
}
