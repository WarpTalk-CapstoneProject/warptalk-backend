using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.Infrastructure.Services;

/// <summary>
/// Streams a chat completion from OpenAI directly over HTTP (Server-Sent Events), no SDK —
/// the same lightweight-client philosophy MeetingService's OpenAIChatTranslator already uses.
/// Milestone A calls this with an empty tool list; the tools param is threaded through now so
/// Milestone B/C only need to populate it, not change this class's shape.
/// </summary>
public sealed class OpenAIAssistantAgent : IAssistantAgent
{
    private const string ChatCompletionsPath = "chat/completions";

    private const string SystemPrompt =
        "You are WarpTalk AI, the assistant embedded in the WarpTalk real-time speech " +
        "translation platform. Answer clearly and concisely. If you don't have enough " +
        "information to answer something about the user's workspace, meetings, or " +
        "terminology, say so honestly instead of guessing.";

    // Bump whenever the system prompt or model changes meaningfully.
    public int PromptVersion => 1;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAIAssistantAgent> _logger;

    public string ModelName { get; }

    public OpenAIAssistantAgent(HttpClient httpClient, IConfiguration configuration, ILogger<OpenAIAssistantAgent> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new ArgumentNullException("OpenAI:ApiKey");
        ModelName = configuration["OpenAI:AssistantModel"] ?? "gpt-4o-mini";

        _httpClient.BaseAddress ??= new Uri("https://api.openai.com/v1/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async IAsyncEnumerable<AssistantAgentEvent> RunAsync(
        IReadOnlyList<AssistantChatTurn> history,
        IReadOnlyList<AssistantToolDefinition> availableTools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<ChatCompletionMessage> { new("system", SystemPrompt) };
        messages.AddRange(history.Select(turn => new ChatCompletionMessage(turn.Role, turn.Content)));

        var requestBody = new ChatCompletionRequest(
            Model: ModelName,
            Temperature: 0.4,
            Stream: true,
            Messages: messages.ToArray());

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
        {
            Content = JsonContent.Create(requestBody),
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("OpenAI streaming chat request failed. Status: {StatusCode}, Body: {Body}", response.StatusCode, body);
            throw new InvalidOperationException("The assistant model is currently unavailable.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var finalText = new StringBuilder();

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
                break;

            string? delta;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var deltaElement = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                delta = deltaElement.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse OpenAI stream chunk: {Payload}", payload);
                continue;
            }

            if (string.IsNullOrEmpty(delta))
                continue;

            finalText.Append(delta);
            yield return new AssistantTextDelta(delta);
        }

        yield return new AssistantCompleted(finalText.ToString());
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("messages")] ChatCompletionMessage[] Messages);

    private sealed record ChatCompletionMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}
