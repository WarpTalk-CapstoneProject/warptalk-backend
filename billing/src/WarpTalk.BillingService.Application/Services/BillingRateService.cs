using System;
using WarpTalk.BillingService.Domain.Constants;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
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

    public Task<Result<ServiceRatesDto>> GetServiceRatesAsync(CancellationToken cancellationToken = default)
    {
        var dto = new ServiceRatesDto(
            SttPerSecond: BillingRateHelper.GetRate(_configuration, BillingRateConstants.Keys.FullSttPerSecond, BillingRateConstants.Defaults.SttPerSecond),
            TranslationPer100Chars: BillingRateHelper.GetRate(_configuration, BillingRateConstants.Keys.FullTranslationPer100Chars, BillingRateConstants.Defaults.TranslationPer100Chars),
            StandardTtsPerSecond: BillingRateHelper.GetRate(_configuration, BillingRateConstants.Keys.FullStandardTtsPerSecond, BillingRateConstants.Defaults.StandardTtsPerSecond),
            VoiceClonePerSecond: BillingRateHelper.GetRate(_configuration, BillingRateConstants.Keys.FullVoiceClonePerSecond, BillingRateConstants.Defaults.VoiceClonePerSecond),
            AiAssistantInputPer1000Tokens: BillingRateHelper.GetRate(_configuration, BillingRateConstants.Keys.FullAiAssistantInputPer1000Tokens, BillingRateConstants.Defaults.AiAssistantInputPer1000Tokens),
            AiAssistantOutputPer1000Tokens: BillingRateHelper.GetRate(_configuration, BillingRateConstants.Keys.FullAiAssistantOutputPer1000Tokens, BillingRateConstants.Defaults.AiAssistantOutputPer1000Tokens)
        );
        return Task.FromResult(Result.Success(dto));
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
                return Result.Failure<ServiceRatesDto>(BillingMessageConstants.ApiErrorMessages.BillingRateValuesInvalid, ErrorCodes.ValidationError);
            }

            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(appSettingsPath))
                return Result.Failure<ServiceRatesDto>(BillingMessageConstants.ApiErrorMessages.BillingAppSettingsNotFound, ErrorCodes.InternalServerError);

            var oldRates = (await GetServiceRatesAsync(cancellationToken)).Value;

            var json = await File.ReadAllTextAsync(appSettingsPath, cancellationToken);
            var doc = JsonDocument.Parse(json);
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == BillingRateConstants.SectionName)
                    continue;
                prop.WriteTo(writer);
            }

            writer.WritePropertyName(BillingRateConstants.SectionName);
            writer.WriteStartObject();
            writer.WriteNumber(BillingRateConstants.Keys.SttPerSecond, request.SttPerSecond);
            writer.WriteNumber(BillingRateConstants.Keys.TranslationPer100Chars, request.TranslationPer100Chars);
            writer.WriteNumber(BillingRateConstants.Keys.StandardTtsPerSecond, request.StandardTtsPerSecond);
            writer.WriteNumber(BillingRateConstants.Keys.VoiceClonePerSecond, request.VoiceClonePerSecond);
            writer.WriteNumber(BillingRateConstants.Keys.AiAssistantInputPer1000Tokens, request.AiAssistantInputPer1000Tokens);
            writer.WriteNumber(BillingRateConstants.Keys.AiAssistantOutputPer1000Tokens, request.AiAssistantOutputPer1000Tokens);
            writer.WriteEndObject();

            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);

            var updatedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            await File.WriteAllTextAsync(appSettingsPath, updatedJson, cancellationToken);

            if (_configuration is IConfigurationRoot configRoot)
                configRoot.Reload();

            _logger.LogInformation(BillingMessageConstants.LogMessages.BillingRatesUpdated);

            var savedRates = await GetServiceRatesAsync(cancellationToken);
            await BillingRateHelper.NotifyWorkspaceOwnersAsync(
                new BillingRateNotificationRequest(
                    _unitOfWork,
                    _notificationClient,
                    _logger,
                    oldRates,
                    request),
                cancellationToken);
            return savedRates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorUpdatingServiceRates);
            return Result.Failure<ServiceRatesDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

}
