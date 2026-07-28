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

public record PricingConfigDto(
    decimal FxRateUsdVnd,
    decimal CreditValueVnd,
    string Formula,
    string ResolverKey);

public record UpdatePricingConfigRequest(
    decimal FxRateUsdVnd,
    decimal CreditValueVnd);
