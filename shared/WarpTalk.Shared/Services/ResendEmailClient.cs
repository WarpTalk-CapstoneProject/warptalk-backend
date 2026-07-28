using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Interfaces;

namespace WarpTalk.Shared.Services;

public class ResendEmailClient : IResendEmailClient
{
    private readonly HttpClient _httpClient;
    private readonly ResendSettings _settings;
    private readonly ILogger<ResendEmailClient> _logger;

    public ResendEmailClient(
        HttpClient httpClient,
        IOptions<ResendSettings> settings,
        ILogger<ResendEmailClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken ct = default)
    {
        try
        {
            var apiKey = !string.IsNullOrWhiteSpace(_settings.ApiKey)
                ? _settings.ApiKey
                : Environment.GetEnvironmentVariable("RESEND_API_KEY");

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("Resend API key is missing. Skipping email send to {To}", request.To);
                return new SendEmailResponse(false, null, "Resend API key is missing.");
            }

            var from = !string.IsNullOrWhiteSpace(request.From)
                ? request.From
                : $"{_settings.FromName} <{_settings.FromEmail}>";

            var payload = new
            {
                from,
                to = new[] { request.To },
                subject = request.Subject,
                html = request.HtmlBody,
                text = request.TextBody
            };

            var json = JsonSerializer.Serialize(payload);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Resend API request failed with status {StatusCode}: {Response}", response.StatusCode, responseContent);
                return new SendEmailResponse(false, null, $"Resend API failed with status {response.StatusCode}: {responseContent}");
            }

            using var doc = JsonDocument.Parse(responseContent);
            var messageId = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            _logger.LogInformation("Successfully sent email via Resend to {To}. MessageId: {MessageId}", request.To, messageId);
            return new SendEmailResponse(true, messageId, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error occurred while sending email via Resend to {To}", request.To);
            return new SendEmailResponse(false, null, ex.Message);
        }
    }
}
