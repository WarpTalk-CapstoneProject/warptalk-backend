using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class IdempotencyRepository : GenericRepository<IdempotencyRecord>, IIdempotencyRepository
{
    private readonly BillingDbContext _db;

    public IdempotencyRepository(BillingDbContext db) : base(db)
    {
        _db = db;
    }

    public Task<IdempotencyRecord?> GetAsync(string key, string operation, CancellationToken ct = default)
        => _db.IdempotencyRecords.FirstOrDefaultAsync(
            record => record.Key == key && record.Operation == operation,
            ct);
}