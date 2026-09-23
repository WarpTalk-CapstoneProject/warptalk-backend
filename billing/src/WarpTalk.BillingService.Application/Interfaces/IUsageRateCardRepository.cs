using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IUsageRateCardRepository
{

    Task<IReadOnlyList<UsageRateCardDto>> GetActiveRateCardsAsync(CancellationToken cancellationToken = default);
    Task<bool> RateCardIdentityExistsAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default);
    Task<UsageRateCardDto> UpsertRateCardAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default);

    /// <summary>Retires one row. Returns null when no row has that id.</summary>
    Task<UsageRateCardDto?> DeactivateRateCardAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the provider cost of one internal credit-unit (CRD) card. A card with no cost yet is
    /// filled in place — the value then applies to the usage already settled on it; a card that
    /// already has a different cost is superseded by a copy carrying the new one, so settled usage
    /// keeps the cost that applied to it. Returns null when no row has that id.
    /// </summary>
    Task<RateCardProviderCostOutcome?> SetCreditRateCardProviderCostAsync(
        Guid id, decimal providerUnitCostUsd, CancellationToken cancellationToken = default);

    Task<decimal> ReadPricingConfigValueAsync(string key, decimal defaultValue, CancellationToken cancellationToken = default);
    Task UpsertPricingConfigValueAsync(string key, decimal value, CancellationToken cancellationToken = default);
    
    // Transaction management for multiple config updates
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IUsageRateCardRepository.SetCreditRateCardProviderCostAsync"/> did.</summary>
public enum RateCardProviderCostChange
{
    /// <summary>The card is not a CRD card, is retired, or has no unit — nothing was written.</summary>
    Refused,
    /// <summary>The card had no cost; it was recorded on the card itself.</summary>
    Recorded,
    /// <summary>The card already had this exact cost.</summary>
    Unchanged,
    /// <summary>The card had another cost; it was closed and a copy with the new cost opened.</summary>
    Superseded,
}

public sealed record RateCardProviderCostOutcome(RateCardProviderCostChange Change, UsageRateCardDto Card);

