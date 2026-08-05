using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class UsageRateCardRepository : IUsageRateCardRepository
{
    private const string DefaultCurrency = "VND";
    private const string UpsertNotes = "Updated from admin pricing controls";

    private readonly BillingDbContext _context;
    private IDbContextTransaction? _currentTransaction;

    public UsageRateCardRepository(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<UsageRateCardDto>> GetActiveRateCardsAsync(CancellationToken cancellationToken = default)
    {
        // Ordering matches the previous SQL: the coalesced-to-empty columns sort
        // first, then the nullable language codes. PostgreSQL already sorts NULLs
        // last for ASC, so the old explicit NULLS LAST was redundant.
        var rows = await _context.UsageRateCards
            .AsNoTracking()
            .Where(e => e.EffectiveTo == null)
            .OrderBy(e => e.ChargeType)
            .ThenBy(e => e.Unit ?? string.Empty)
            .ThenBy(e => e.Provider ?? string.Empty)
            .ThenBy(e => e.Model ?? string.Empty)
            .ThenBy(e => e.SourceLanguageCode)
            .ThenBy(e => e.TargetLanguageCode)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    public async Task<bool> RateCardIdentityExistsAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default)
    {
        return await FilterByIdentity(_context.UsageRateCards.AsNoTracking(), request)
            .AnyAsync(cancellationToken);
    }

    public async Task<UsageRateCardDto> UpsertRateCardAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default)
    {
        // Rate cards are append-only: supersede the current active row for this
        // identity, then insert the new priced row. The unique index
        // ux_usage_rate_card_active_lookup allows at most one such row, but the
        // loop keeps this correct if historical data ever violated that.
        var supersededAt = DateTime.UtcNow;

        var current = await FilterByIdentity(_context.UsageRateCards, request)
            .Where(e => e.IsActive && e.EffectiveTo == null)
            .ToListAsync(cancellationToken);

        foreach (var row in current)
        {
            row.IsActive = false;
            row.EffectiveTo = supersededAt;
        }

        // Flush the supersede before inserting. ux_usage_rate_card_active_lookup is
        // a partial unique index over (identity) WHERE is_active AND effective_to IS
        // NULL, so the old row must stop being active before the new one exists.
        // Batching both into one SaveChangesAsync would leave that on EF's command
        // ordering, which is an implementation detail, not a contract. Both
        // statements run inside the caller's transaction, so this stays atomic.
        if (current.Count > 0)
            await _context.SaveChangesAsync(cancellationToken);

        var inserted = new UsageRateCard
        {
            ChargeType = request.ChargeType.Trim(),
            Unit = request.Unit.Trim(),
            Currency = NormalizeCurrency(request.Currency),
            Provider = request.Provider.Trim(),
            Model = request.Model.Trim(),
            SourceLanguageCode = NormalizeLanguageCode(request.SourceLanguageCode),
            TargetLanguageCode = NormalizeLanguageCode(request.TargetLanguageCode),
            ProviderUnitCost = request.ProviderUnitCostUsd,
            MarkupMultiplier = request.MarkupMultiplier,
            UnitPrice = request.UnitPrice,
            EffectiveFrom = supersededAt,
            IsActive = request.IsActive ?? true,
            Notes = UpsertNotes
        };

        _context.UsageRateCards.Add(inserted);
        await _context.SaveChangesAsync(cancellationToken);

        // Id and any other store-generated values are populated by EF from the
        // INSERT's RETURNING clause, which is what the previous hand-written
        // RETURNING list was doing.
        return ToDto(inserted);
    }

    public async Task<decimal> ReadPricingConfigValueAsync(string key, decimal defaultValue, CancellationToken cancellationToken = default)
    {
        var value = await _context.BillingPricingConfigs
            .AsNoTracking()
            .Where(e => e.Key == key)
            .Select(e => (decimal?)e.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return value ?? defaultValue;
    }

    public async Task UpsertPricingConfigValueAsync(string key, decimal value, CancellationToken cancellationToken = default)
    {
        // Replaces INSERT ... ON CONFLICT, which EF Core cannot express. Pricing
        // keys are written only from the admin surface, and the callers wrap a
        // batch of these in one transaction, so a read-then-write is equivalent;
        // a genuinely concurrent insert of the same key fails loudly on the
        // primary key rather than silently overwriting.
        var existing = await _context.BillingPricingConfigs
            .FirstOrDefaultAsync(e => e.Key == key, cancellationToken);

        if (existing is null)
        {
            _context.BillingPricingConfigs.Add(new BillingPricingConfig
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        // Uses the DbContext's own transaction so writes made through the change
        // tracker and this transaction are the same unit of work. The previous
        // implementation opened a DbTransaction directly on the connection,
        // which ran alongside (and independently of) the context's transaction.
        _currentTransaction ??= await _context.Database.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is null)
            return;

        await _currentTransaction.CommitAsync(cancellationToken);
        await _currentTransaction.DisposeAsync();
        _currentTransaction = null;
    }

    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is null)
            return;

        await _currentTransaction.RollbackAsync(cancellationToken);
        await _currentTransaction.DisposeAsync();
        _currentTransaction = null;
    }

    /// <summary>
    /// Reproduces the SQL identity match, including its IS NOT DISTINCT FROM
    /// handling of the nullable language codes: a null code must match only rows
    /// whose code is also null, which <c>== null</c> in LINQ does not guarantee
    /// once the value is parameterised.
    /// </summary>
    private static IQueryable<UsageRateCard> FilterByIdentity(
        IQueryable<UsageRateCard> query,
        UpsertUsageRateCardRequest request)
    {
        var chargeType = request.ChargeType.Trim();
        var unit = request.Unit.Trim();
        var currency = NormalizeCurrency(request.Currency);
        var provider = request.Provider.Trim();
        var model = request.Model.Trim();
        var sourceLanguageCode = NormalizeLanguageCode(request.SourceLanguageCode);
        var targetLanguageCode = NormalizeLanguageCode(request.TargetLanguageCode);

        query = query.Where(e =>
            e.ChargeType == chargeType &&
            e.Unit == unit &&
            e.Currency == currency &&
            e.Provider == provider &&
            e.Model == model);

        query = sourceLanguageCode is null
            ? query.Where(e => e.SourceLanguageCode == null)
            : query.Where(e => e.SourceLanguageCode == sourceLanguageCode);

        query = targetLanguageCode is null
            ? query.Where(e => e.TargetLanguageCode == null)
            : query.Where(e => e.TargetLanguageCode == targetLanguageCode);

        return query;
    }

    private static UsageRateCardDto ToDto(UsageRateCard entity)
    {
        return new UsageRateCardDto(
            entity.Id,
            entity.ChargeType,
            entity.Unit ?? string.Empty,
            entity.Provider ?? string.Empty,
            entity.Model ?? string.Empty,
            entity.SourceLanguageCode,
            entity.TargetLanguageCode,
            entity.UnitPrice,
            entity.Currency,
            entity.ProviderUnitCost,
            entity.MarkupMultiplier,
            entity.EffectiveFrom,
            entity.EffectiveTo,
            entity.IsActive);
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
}
