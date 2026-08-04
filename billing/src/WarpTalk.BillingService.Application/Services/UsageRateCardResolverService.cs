using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Caching.Memory;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class UsageRateCardResolverService : IUsageRateCardResolverService
{
    private const string CacheKey = "ActiveRateCards";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IUsageRateCardRepository _repository;
    private readonly IMemoryCache _cache;

    public UsageRateCardResolverService(IUsageRateCardRepository repository, IMemoryCache cache)
    {
        _repository = repository;
        _cache = cache;
    }

    public async Task<Result<UsageRateCardDto>> ResolveRateCardAsync(
        string chargeType,
        string unit,
        string currency,
        string? sourceLanguageCode,
        string? targetLanguageCode,
        CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue(CacheKey, out IReadOnlyList<UsageRateCardDto>? rateCards) || rateCards is null)
        {
            rateCards = await _repository.GetActiveRateCardsAsync(cancellationToken);
            _cache.Set(CacheKey, rateCards, CacheTtl);
        }

        var matchingCards = rateCards
            .Where(rateCard =>
                rateCard.ChargeType.Equals(chargeType, StringComparison.OrdinalIgnoreCase) &&
                rateCard.Unit.Equals(unit, StringComparison.OrdinalIgnoreCase) &&
                rateCard.Currency.Equals(currency, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var exactMatch = matchingCards.FirstOrDefault(rateCard =>
            !string.IsNullOrEmpty(rateCard.SourceLanguageCode) &&
            rateCard.SourceLanguageCode.Equals(sourceLanguageCode, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(rateCard.TargetLanguageCode) &&
            rateCard.TargetLanguageCode.Equals(targetLanguageCode, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null) return Result.Success(exactMatch);

        var targetMatch = matchingCards.FirstOrDefault(rateCard =>
            string.IsNullOrEmpty(rateCard.SourceLanguageCode) &&
            !string.IsNullOrEmpty(rateCard.TargetLanguageCode) &&
            rateCard.TargetLanguageCode.Equals(targetLanguageCode, StringComparison.OrdinalIgnoreCase));
        if (targetMatch is not null) return Result.Success(targetMatch);

        var sourceMatch = matchingCards.FirstOrDefault(rateCard =>
            !string.IsNullOrEmpty(rateCard.SourceLanguageCode) &&
            rateCard.SourceLanguageCode.Equals(sourceLanguageCode, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(rateCard.TargetLanguageCode));
        if (sourceMatch is not null) return Result.Success(sourceMatch);

        var baseMatch = matchingCards.FirstOrDefault(rateCard =>
            string.IsNullOrEmpty(rateCard.SourceLanguageCode) &&
            string.IsNullOrEmpty(rateCard.TargetLanguageCode));
        if (baseMatch is not null) return Result.Success(baseMatch);

        return Result.Failure<UsageRateCardDto>(
            $"Rate card not found for ChargeType={chargeType}, Unit={unit}",
            "RATE_CARD_NOT_FOUND");
    }
}
