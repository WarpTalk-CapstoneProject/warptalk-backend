using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class BillingPolicyRepository : IBillingPolicyRepository
{
    private readonly BillingDbContext _context;

    public BillingPolicyRepository(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<decimal> ReadPolicyValueAsync(string key, decimal seedValue, CancellationToken cancellationToken = default)
    {
        var row = await _context.BillingPolicyConfigs
            .AsNoTracking()
            .Where(e => e.Key == key)
            .Select(e => (decimal?)e.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return row ?? seedValue;
    }

    public async Task UpsertPolicyValueAsync(string key, decimal value, CancellationToken cancellationToken = default)
    {
        // The previous implementation used INSERT ... ON CONFLICT, which EF Core
        // cannot express. Read-then-write is equivalent here because policy keys
        // are edited only from the single-writer admin surface; a concurrent
        // insert of the same key surfaces as a unique-violation DbUpdateException
        // rather than silently overwriting.
        var existing = await _context.BillingPolicyConfigs
            .FirstOrDefaultAsync(e => e.Key == key, cancellationToken);

        if (existing is null)
        {
            _context.BillingPolicyConfigs.Add(new BillingPolicyConfig
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
}
