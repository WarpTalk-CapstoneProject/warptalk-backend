using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Infrastructure.Services;

/// <summary>
/// Translates chat messages via OpenAI's Chat Completions API directly (no SDK —
/// matches the AI pipeline's lightweight-client philosophy). Uses a small model
/// (gpt-4o-mini by default) since chat messages are short and this runs synchronously
/// on the request path — deterministic output (temperature 0) keeps repeated phrases
/// consistent for the same reason the speech translation worker uses it.
/// </summary>
public sealed class OpenAIChatTranslator : IChatTranslator
{
    private const string ChatCompletionsPath = "chat/completions";

    // Bump whenever the system prompt below changes meaningfully — see IChatTranslator.PromptVersion.
    public int PromptVersion => 1;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAIChatTranslator> _logger;

    public string ModelName { get; }

    public OpenAIChatTranslator(HttpClient httpClient, IConfiguration configuration, ILogger<OpenAIChatTranslator> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new ArgumentNullException("OpenAI:ApiKey");
        ModelName = configuration["OpenAI:ChatTranslationModel"] ?? "gpt-4o-mini";

        _httpClient.BaseAddress ??= new Uri("https://api.openai.com/v1/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<Result<string>> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken ct = default)
    {
        var systemPrompt =
            $"You are a translation engine embedded in a chat application. Translate the user's " +
            $"message from {sourceLanguage} to {targetLanguage}. Rules: return ONLY the translated " +
            $"text. Do not add quotes, explanations, notes, or the original text. Preserve tone, " +
            $"emojis, and formatting. If the message is already in {targetLanguage}, return it unchanged.";

        var request = new ChatCompletionRequest(
            Model: ModelName,
            Temperature: 0,
            MaxTokens: 512,
            Messages: new[]
            {
                new ChatCompletionMessage("system", systemPrompt),
                new ChatCompletionMessage("user", text),
            });

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(ChatCompletionsPath, request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "OpenAI chat translation request failed. Status: {StatusCode}, Body: {Body}",
                    response.StatusCode, body);
                return Result.Failure<string>("Translation service is currently unavailable.", "TRANSLATION_FAILED");
            }

            var payload = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct);
            var translated = payload?.Choices is { Length: > 0 }
                ? payload.Choices[0].Message.Content?.Trim()
                : null;

            if (string.IsNullOrWhiteSpace(translated))
            {
                _logger.LogError("OpenAI chat translation returned an empty completion.");
                return Result.Failure<string>("Translation service returned an empty result.", "TRANSLATION_FAILED");
            }

            return Result.Success(translated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling OpenAI for chat translation.");
            return Result.Failure<string>("An unexpected error occurred while translating the message.", "TRANSLATION_FAILED");
        }
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("messages")] ChatCompletionMessage[] Messages);

    private sealed record ChatCompletionMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] ChatCompletionChoice[] Choices);

    private sealed record ChatCompletionChoice(
        [property: JsonPropertyName("message")] ChatCompletionMessage Message);
}
