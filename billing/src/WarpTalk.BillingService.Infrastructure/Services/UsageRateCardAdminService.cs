using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public sealed class UsageRateCardAdminService : IUsageRateCardAdminService
{
    private const string DefaultCurrency = "VND";
    private const string FxRateConfigKey = "fx_rate_usd_vnd";
    private const string CreditValueConfigKey = "credit_value_vnd";
    private const string MinimumPricePerCreditVndConfigKey = "minimum_price_per_credit_vnd";
    private const string MinimumContractPriceVndConfigKey = "minimum_contract_price_vnd";
    private const string MinimumContractPriceUsdConfigKey = "minimum_contract_price_usd";
    private const string SalesUsageWeightConfigKey = "sales_usage_weight";
    private const string SalesMembersWeightConfigKey = "sales_members_weight";
    private const string SalesLanguagesWeightConfigKey = "sales_languages_weight";
    private const string SalesAiServicesWeightConfigKey = "sales_ai_services_weight";
    private const string DefaultOverageCapRatioConfigKey = "default_overage_cap_ratio";
    private const string DefaultInvoiceTermsDaysConfigKey = "default_invoice_terms_days";
    private const string DefaultInvoiceGraceHoursConfigKey = "default_invoice_grace_hours";
    private const string PricingFormula = "provider_unit_cost_usd * fx_rate_usd_vnd * markup_multiplier / credit_value_vnd";
    private const string ResolverKey = "provider + model + charge_type + unit + source_language_code + target_language_code";

    private static readonly HashSet<RateCardIdentity> RegisteredBillingIdentities = new()
    {
        CreateRateCardIdentity("AI_ASSISTANT", "token_in", DefaultCurrency, "openai", "gpt-4.1"),
        CreateRateCardIdentity("AI_ASSISTANT", "token_in_cached", DefaultCurrency, "openai", "gpt-4.1"),
        CreateRateCardIdentity("AI_ASSISTANT", "token_out", DefaultCurrency, "openai", "gpt-4.1"),
        CreateRateCardIdentity("AI_SUMMARY", "token_in", DefaultCurrency, "openai", "gpt-4o-mini"),
        CreateRateCardIdentity("AI_SUMMARY", "token_in_cached", DefaultCurrency, "openai", "gpt-4o-mini"),
        CreateRateCardIdentity("AI_SUMMARY", "token_out", DefaultCurrency, "openai", "gpt-4o-mini"),
        CreateRateCardIdentity("AUDIO_DUBBING_STANDARD", "character", DefaultCurrency, "cartesia", "sonic-3.5"),
        CreateRateCardIdentity("AUDIO_DUBBING_VOICE_CLONE", "character", DefaultCurrency, "cartesia", "sonic-3.5-clone"),
        CreateRateCardIdentity("STT", "second", DefaultCurrency, "openai", "gpt-4o-transcribe"),
        CreateRateCardIdentity("TRANSLATION", "token_in", DefaultCurrency, "openai", "gpt-4.1-mini"),
        CreateRateCardIdentity("TRANSLATION", "token_in_cached", DefaultCurrency, "openai", "gpt-4.1-mini"),
        CreateRateCardIdentity("TRANSLATION", "token_out", DefaultCurrency, "openai", "gpt-4.1-mini"),
        CreateRateCardIdentity("VOICE_CLONE_ENROLLMENT", "profile", DefaultCurrency, "cartesia", "cartesia-localizing-voice"),
    };

    private readonly IUsageRateCardRepository _repository;
    private readonly ILogger<UsageRateCardAdminService> _logger;

    public UsageRateCardAdminService(
        IUsageRateCardRepository repository,
        ILogger<UsageRateCardAdminService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<UsageRateCardDto>>> GetActiveRateCardsAsync(CancellationToken cancellationToken = default)
    {
        try
        {

            var rows = await _repository.GetActiveRateCardsAsync(cancellationToken);
            return Result.Success(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading usage rate card");
            return Result.Failure<IReadOnlyList<UsageRateCardDto>>("Unable to load usage rate card.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<UsageRateCardDto>> UpsertRateCardAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsValid(request))
            return Result.Failure<UsageRateCardDto>("Invalid usage rate card request.", ErrorCodes.ValidationError);

        if (!IsRegisteredBillingIdentity(request))
        {
            return Result.Failure<UsageRateCardDto>(
                "Usage rate-card identity is not registered. Add new billing identities through a migration/backend release first.",
                ErrorCodes.ValidationError);
        }

        try
        {

            await _repository.BeginTransactionAsync(cancellationToken);

            var identityExists = await _repository.RateCardIdentityExistsAsync(request, cancellationToken);
            if (!identityExists)
            {
                await _repository.RollbackTransactionAsync(cancellationToken);
                return Result.Failure<UsageRateCardDto>(
                    "Usage rate-card identity is not registered. Add new billing identities through a migration/backend release first.",
                    ErrorCodes.ValidationError);
            }

            var inserted = await _repository.UpsertRateCardAsync(request, cancellationToken);
            await _repository.CommitTransactionAsync(cancellationToken);
            return Result.Success(inserted);
        }
        catch (Exception ex)
        {
            await _repository.RollbackTransactionAsync(cancellationToken);
            _logger.LogError(ex, "Error updating usage rate card");
            return Result.Failure<UsageRateCardDto>("Unable to update usage rate card.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PricingConfigDto>> GetPricingConfigAsync(CancellationToken cancellationToken = default)
    {
        try
        {


            var fxRate = await _repository.ReadPricingConfigValueAsync(FxRateConfigKey, SubscriptionConstants.RateCardDefaults.FxRateUsdVnd, cancellationToken);
            var creditValue = await _repository.ReadPricingConfigValueAsync(CreditValueConfigKey, SubscriptionConstants.RateCardDefaults.CreditValueVnd, cancellationToken);
            var minimumPricePerCredit = await _repository.ReadPricingConfigValueAsync(MinimumPricePerCreditVndConfigKey, SubscriptionConstants.PlanDefaults.PriceFloorPerCredit, cancellationToken);
            var minimumContractPrice = await _repository.ReadPricingConfigValueAsync(MinimumContractPriceVndConfigKey, SubscriptionConstants.PlanDefaults.MinimumVndPlanPrice, cancellationToken);
            var minimumContractPriceUsd = await _repository.ReadPricingConfigValueAsync(MinimumContractPriceUsdConfigKey, SubscriptionConstants.PlanDefaults.MinimumUsdPlanPrice, cancellationToken);
            var salesUsageWeight = await _repository.ReadPricingConfigValueAsync(SalesUsageWeightConfigKey, SubscriptionConstants.RateCardDefaults.SalesUsageWeight, cancellationToken);
            var salesMembersWeight = await _repository.ReadPricingConfigValueAsync(SalesMembersWeightConfigKey, SubscriptionConstants.RateCardDefaults.SalesMembersWeight, cancellationToken);
            var salesLanguagesWeight = await _repository.ReadPricingConfigValueAsync(SalesLanguagesWeightConfigKey, SubscriptionConstants.RateCardDefaults.SalesLanguagesWeight, cancellationToken);
            var salesAiServicesWeight = await _repository.ReadPricingConfigValueAsync(SalesAiServicesWeightConfigKey, SubscriptionConstants.RateCardDefaults.SalesAiServicesWeight, cancellationToken);
            var defaultOverageCapRatio = await _repository.ReadPricingConfigValueAsync(DefaultOverageCapRatioConfigKey, SubscriptionConstants.RateCardDefaults.DefaultOverageCapRatio, cancellationToken);
            var defaultInvoiceTermsDays = await _repository.ReadPricingConfigValueAsync(DefaultInvoiceTermsDaysConfigKey, SubscriptionConstants.PlanDefaults.InvoiceTermsDays, cancellationToken);
            var defaultInvoiceGraceHours = await _repository.ReadPricingConfigValueAsync(DefaultInvoiceGraceHoursConfigKey, SubscriptionConstants.PlanDefaults.InvoiceGraceHours, cancellationToken);

            return Result.Success(CreatePricingConfig(
                fxRate,
                creditValue,
                minimumPricePerCredit,
                minimumContractPrice,
                minimumContractPriceUsd,
                salesUsageWeight,
                salesMembersWeight,
                salesLanguagesWeight,
                salesAiServicesWeight,
                defaultOverageCapRatio,
                defaultInvoiceTermsDays,
                defaultInvoiceGraceHours));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load billing pricing config");
            return Result.Failure<PricingConfigDto>("Unable to load billing pricing config.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PricingConfigDto>> UpdatePricingConfigAsync(UpdatePricingConfigRequest request, CancellationToken cancellationToken = default)
    {
        if (request.FxRateUsdVnd <= 0 ||
            request.CreditValueVnd <= 0 ||
            request.MinimumPricePerCreditVnd <= 0 ||
            request.MinimumContractPriceVnd <= 0 ||
            request.MinimumContractPriceUsd <= 0 ||
            request.SalesUsageWeight < 0 ||
            request.SalesMembersWeight < 0 ||
            request.SalesLanguagesWeight < 0 ||
            request.SalesAiServicesWeight < 0 ||
            request.DefaultOverageCapRatio < 0 ||
            request.DefaultOverageCapRatio > 1 ||
            request.DefaultInvoiceTermsDays <= 0 ||
            request.DefaultInvoiceGraceHours <= 0)
            return Result.Failure<PricingConfigDto>("Pricing config values must be positive.", ErrorCodes.ValidationError);

        var salesWeightTotal = request.SalesUsageWeight + request.SalesMembersWeight + request.SalesLanguagesWeight + request.SalesAiServicesWeight;
        if (salesWeightTotal <= 0)
            return Result.Failure<PricingConfigDto>("Sales pricing weights must have a positive total.", ErrorCodes.ValidationError);

        try
        {

            await _repository.BeginTransactionAsync(cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(FxRateConfigKey, request.FxRateUsdVnd, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(CreditValueConfigKey, request.CreditValueVnd, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(MinimumPricePerCreditVndConfigKey, request.MinimumPricePerCreditVnd, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(MinimumContractPriceVndConfigKey, request.MinimumContractPriceVnd, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(MinimumContractPriceUsdConfigKey, request.MinimumContractPriceUsd, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(SalesUsageWeightConfigKey, request.SalesUsageWeight, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(SalesMembersWeightConfigKey, request.SalesMembersWeight, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(SalesLanguagesWeightConfigKey, request.SalesLanguagesWeight, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(SalesAiServicesWeightConfigKey, request.SalesAiServicesWeight, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(DefaultOverageCapRatioConfigKey, request.DefaultOverageCapRatio, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(DefaultInvoiceTermsDaysConfigKey, request.DefaultInvoiceTermsDays, cancellationToken);
            await _repository.UpsertPricingConfigValueAsync(DefaultInvoiceGraceHoursConfigKey, request.DefaultInvoiceGraceHours, cancellationToken);
            await _repository.CommitTransactionAsync(cancellationToken);

            return Result.Success(CreatePricingConfig(
                request.FxRateUsdVnd,
                request.CreditValueVnd,
                request.MinimumPricePerCreditVnd,
                request.MinimumContractPriceVnd,
                request.MinimumContractPriceUsd,
                request.SalesUsageWeight,
                request.SalesMembersWeight,
                request.SalesLanguagesWeight,
                request.SalesAiServicesWeight,
                request.DefaultOverageCapRatio,
                request.DefaultInvoiceTermsDays,
                request.DefaultInvoiceGraceHours));
        }
        catch (Exception ex)
        {
            await _repository.RollbackTransactionAsync(cancellationToken);
            _logger.LogError(ex, "Error updating billing pricing config");
            return Result.Failure<PricingConfigDto>("Unable to update billing pricing config.", ErrorCodes.InternalServerError);
        }
    }

    private static bool IsValid(UpsertUsageRateCardRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.ChargeType) &&
               !string.IsNullOrWhiteSpace(request.Unit) &&
               !string.IsNullOrWhiteSpace(request.Provider) &&
               !string.IsNullOrWhiteSpace(request.Model) &&
               request.UnitPrice >= 0 &&
               (request.ProviderUnitCostUsd is null or >= 0) &&
               (request.MarkupMultiplier is null or >= 0);
    }

    private static string? NormalizeLanguageCode(string? languageCode)
    {
        return string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().ToLowerInvariant();
    }

    private static string NormalizeCurrency(string? currency)
    {
        return string.IsNullOrWhiteSpace(currency)
            ? DefaultCurrency
            : currency.Trim().ToUpperInvariant();
    }

    private static bool IsRegisteredBillingIdentity(UpsertUsageRateCardRequest request)
    {
        return RegisteredBillingIdentities.Contains(CreateRateCardIdentity(
            request.ChargeType,
            request.Unit,
            request.Currency,
            request.Provider,
            request.Model,
            request.SourceLanguageCode,
            request.TargetLanguageCode));
    }

    private static RateCardIdentity CreateRateCardIdentity(
        string chargeType,
        string unit,
        string? currency,
        string provider,
        string model,
        string? sourceLanguageCode = null,
        string? targetLanguageCode = null)
    {
        return new RateCardIdentity(
            chargeType.Trim().ToUpperInvariant(),
            unit.Trim().ToLowerInvariant(),
            NormalizeCurrency(currency),
            provider.Trim().ToLowerInvariant(),
            model.Trim().ToLowerInvariant(),
            NormalizeLanguageCode(sourceLanguageCode),
            NormalizeLanguageCode(targetLanguageCode));
    }

    private static PricingConfigDto CreatePricingConfig(
        decimal fxRateUsdVnd,
        decimal creditValueVnd,
        decimal minimumPricePerCreditVnd,
        decimal minimumContractPriceVnd,
        decimal minimumContractPriceUsd,
        decimal salesUsageWeight,
        decimal salesMembersWeight,
        decimal salesLanguagesWeight,
        decimal salesAiServicesWeight,
        decimal defaultOverageCapRatio,
        decimal defaultInvoiceTermsDays,
        decimal defaultInvoiceGraceHours)
    {
        return new PricingConfigDto(
            fxRateUsdVnd,
            creditValueVnd,
            minimumPricePerCreditVnd,
            minimumContractPriceVnd,
            minimumContractPriceUsd,
            salesUsageWeight,
            salesMembersWeight,
            salesLanguagesWeight,
            salesAiServicesWeight,
            defaultOverageCapRatio,
            defaultInvoiceTermsDays,
            defaultInvoiceGraceHours,
            PricingFormula,
            ResolverKey);
    }

    private readonly record struct RateCardIdentity(
        string ChargeType,
        string Unit,
        string Currency,
        string Provider,
        string Model,
        string? SourceLanguageCode,
        string? TargetLanguageCode);
}
