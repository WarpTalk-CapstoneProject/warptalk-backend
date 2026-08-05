namespace WarpTalk.BillingService.Application.DTOs;

public record UsageRateCardDto(
    Guid Id,
    string ChargeType,
    string Unit,
    string Provider,
    string Model,
    string? SourceLanguageCode,
    string? TargetLanguageCode,
    decimal UnitPrice,
    string Currency,
    decimal? ProviderUnitCostUsd,
    decimal? MarkupMultiplier,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    bool IsActive);

public record UpsertUsageRateCardRequest(
    string ChargeType,
    string Unit,
    string Provider,
    string Model,
    string? SourceLanguageCode,
    string? TargetLanguageCode,
    decimal UnitPrice,
    string Currency,
    decimal? ProviderUnitCostUsd,
    decimal? MarkupMultiplier,
    bool? IsActive = true);

/// <summary>
/// Prices a hypothetical rate change before it is published. FX rate and credit value
/// fall back to the stored pricing config when omitted, so an admin can preview against
/// live economics or against a proposed change to them.
/// </summary>
public record RateCardPreviewRequest(
    decimal ProviderUnitCostUsd,
    decimal MarkupMultiplier,
    decimal Quantity = 1m,
    decimal? FxRateUsdVnd = null,
    decimal? CreditValueVnd = null);

public record RateCardPreviewDto(
    decimal UnitPriceCredits,
    decimal CreditsCharged,
    decimal CustomerPriceVnd,
    decimal ProviderCostVnd,
    decimal MarginVnd,
    decimal MarginRatio,
    decimal FxRateUsdVnd,
    decimal CreditValueVnd,
    string Formula);

public record PricingConfigDto(
    decimal FxRateUsdVnd,
    decimal CreditValueVnd,
    decimal MinimumPricePerCreditVnd,
    decimal MinimumContractPriceVnd,
    decimal MinimumContractPriceUsd,
    decimal SalesUsageWeight,
    decimal SalesMembersWeight,
    decimal SalesLanguagesWeight,
    decimal SalesAiServicesWeight,
    decimal DefaultOverageCapRatio,
    decimal DefaultInvoiceTermsDays,
    decimal DefaultInvoiceGraceHours,
    string Formula,
    string ResolverKey);

public record UpdatePricingConfigRequest(
    decimal FxRateUsdVnd,
    decimal CreditValueVnd,
    decimal MinimumPricePerCreditVnd,
    decimal MinimumContractPriceVnd,
    decimal MinimumContractPriceUsd,
    decimal SalesUsageWeight,
    decimal SalesMembersWeight,
    decimal SalesLanguagesWeight,
    decimal SalesAiServicesWeight,
    decimal DefaultOverageCapRatio,
    decimal DefaultInvoiceTermsDays,
    decimal DefaultInvoiceGraceHours);
