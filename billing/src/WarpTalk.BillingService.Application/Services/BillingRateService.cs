using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class BillingRateService : IBillingRateService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<BillingRateService> _logger;
    private readonly IConfiguration _configuration;
    private readonly INotificationClient? _notificationClient;

    public BillingRateService(
        IUnitOfWork unitOfWork,
        ILogger<BillingRateService> logger,
        IConfiguration configuration,
        INotificationClient? notificationClient = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _configuration = configuration;
        _notificationClient = notificationClient;
    }

    private double GetRate(string key, double fallback) =>
        double.TryParse(_configuration[key], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public Result<ServiceRatesDto> GetServiceRates()
    {
        var dto = new ServiceRatesDto(
            SttPerSecond: GetRate("BillingRates:SttPerSecond", 1.0),
            TranslationPer100Chars: GetRate("BillingRates:TranslationPer100Chars", 1.0),
            StandardTtsPerSecond: GetRate("BillingRates:StandardTtsPerSecond", 1.0),
            VoiceClonePerSecond: GetRate("BillingRates:VoiceClonePerSecond", 1.5),
            AiAssistantInputPer1000Tokens: GetRate("BillingRates:AiAssistantInputPer1000Tokens", 0.5),
            AiAssistantOutputPer1000Tokens: GetRate("BillingRates:AiAssistantOutputPer1000Tokens", 2.0)
        );
        return Result.Success(dto);
    }

    public async Task<Result<ServiceRatesDto>> UpdateServiceRatesAsync(
        UpdateServiceRatesRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.SttPerSecond <= 0 || request.TranslationPer100Chars <= 0 ||
                request.StandardTtsPerSecond <= 0 || request.VoiceClonePerSecond <= 0 ||
                request.AiAssistantInputPer1000Tokens <= 0 || request.AiAssistantOutputPer1000Tokens <= 0)
            {
                return Result.Failure<ServiceRatesDto>("All rate values must be greater than zero.", ErrorCodes.ValidationError);
            }

            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(appSettingsPath))
                return Result.Failure<ServiceRatesDto>("appsettings.json not found on server.", ErrorCodes.InternalServerError);

            var oldRates = GetServiceRates().Value;

            var json = await File.ReadAllTextAsync(appSettingsPath, cancellationToken);
            var doc = JsonDocument.Parse(json);
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "BillingRates")
                    continue;
                prop.WriteTo(writer);
            }

            writer.WritePropertyName("BillingRates");
            writer.WriteStartObject();
            writer.WriteNumber("SttPerSecond", request.SttPerSecond);
            writer.WriteNumber("TranslationPer100Chars", request.TranslationPer100Chars);
            writer.WriteNumber("StandardTtsPerSecond", request.StandardTtsPerSecond);
            writer.WriteNumber("VoiceClonePerSecond", request.VoiceClonePerSecond);
            writer.WriteNumber("AiAssistantInputPer1000Tokens", request.AiAssistantInputPer1000Tokens);
            writer.WriteNumber("AiAssistantOutputPer1000Tokens", request.AiAssistantOutputPer1000Tokens);
            writer.WriteEndObject();

            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);

            var updatedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            await File.WriteAllTextAsync(appSettingsPath, updatedJson, cancellationToken);

            if (_configuration is IConfigurationRoot configRoot)
                configRoot.Reload();

            _logger.LogInformation("BillingRates updated by admin.");

            var savedRates = GetServiceRates();
            await NotifyWorkspaceOwnersAsync(oldRates, request, cancellationToken);
            return savedRates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating service rates");
            return Result.Failure<ServiceRatesDto>("An unexpected error occurred while saving rates.", ErrorCodes.InternalServerError);
        }
    }

    private async Task NotifyWorkspaceOwnersAsync(
        ServiceRatesDto? oldRates,
        UpdateServiceRatesRequest newRates,
        CancellationToken cancellationToken)
    {
        if (_notificationClient is null) return;

        try
        {
            var changes = new List<string>();
            void AddChange(string label, double oldVal, double newVal, string unit)
            {
                if (Math.Abs(oldVal - newVal) > 0.0001)
                    changes.Add($"• {label}: {oldVal:0.##} → {newVal:0.##} {unit}");
            }

            if (oldRates is not null)
            {
                AddChange("Speech-to-Text (STT)",       oldRates.SttPerSecond,           newRates.SttPerSecond,           "credits/sec");
                AddChange("Real-time Translation",      oldRates.TranslationPer100Chars, newRates.TranslationPer100Chars, "credits/100chars");
                AddChange("Text-to-Speech (TTS)",       oldRates.StandardTtsPerSecond,   newRates.StandardTtsPerSecond,   "credits/sec");
                AddChange("Voice Clone TTS",            oldRates.VoiceClonePerSecond,    newRates.VoiceClonePerSecond,    "credits/sec");
                AddChange("AI Assistant (Input)",       oldRates.AiAssistantInputPer1000Tokens, newRates.AiAssistantInputPer1000Tokens, "credits/1k tokens");
                AddChange("AI Assistant (Output)",      oldRates.AiAssistantOutputPer1000Tokens, newRates.AiAssistantOutputPer1000Tokens, "credits/1k tokens");
            }

            if (changes.Count == 0) return;

            var changedList  = string.Join("\n", changes);
            var body = $"WarpTalk has updated the AI service credit rates that apply to your workspace:\n\n{changedList}\n\nNew rates are effective immediately for all future sessions.";

            var ownerUserIds = new List<Guid>();
            try
            {
                using var conn = _unitOfWork.GetDbConnection();
                var wasOpen = conn.State == System.Data.ConnectionState.Open;
                if (!wasOpen) await conn.OpenAsync(cancellationToken);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT user_id FROM subscription.subscriptions WHERE is_active = true AND deleted_at IS NULL AND user_id IS NOT NULL";
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0))
                        ownerUserIds.Add(reader.GetGuid(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load workspace owner IDs for rate change notification.");
                return;
            }

            _logger.LogInformation("Sending AI rate change notifications to {Count} workspace owners.", ownerUserIds.Count);

            var tasks = ownerUserIds.Select(userId =>
            {
                var metadata = new Dictionary<string, string> { { "changed_services", changes.Count.ToString() } };
                return _notificationClient.SendNotificationAsync(
                    userId,
                    "billing.rate_change",
                    "AI Service Rates Updated",
                    body,
                    "/billing",
                    metadata,
                    cancellationToken
                );
            });

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify workspace owners about rate update");
        }
    }
}
