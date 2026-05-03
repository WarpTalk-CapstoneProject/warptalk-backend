using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;

public class UsageEventService : IUsageEventService
{
    private readonly IUsageEventRepository _repo;
    private readonly IUnitOfWork _uow;

    public UsageEventService(IUsageEventRepository repo, IUnitOfWork uow)
    {
        _repo = repo;
        _uow = uow;
    }

    public async Task AddAsync(UsageEvent entity, CancellationToken ct = default)
    {
        await _repo.AddAsync(entity, ct);
        await _uow.SaveChangesAsync(ct);
    }

    public Task<bool> ExistsByIdempotencyKeyAsync(string key, CancellationToken ct = default)
        => _repo.ExistsByIdempotencyKeyAsync(key, ct);

    public Task<IReadOnlyList<UsageEvent>> GetPendingAsync(int take, CancellationToken ct = default)
        => _repo.GetPendingAsync(take, ct);

    public async Task MarkProcessedAsync(Guid eventId, CancellationToken ct = default)
    {
        var entity = await _repo.GetByIdAsync(eventId, ct);
        if (entity == null) return;

        entity.Status = UsageEventStatus.Processed;
        entity.ProcessedAt = DateTime.UtcNow;

        await _repo.UpdateAsync(entity, ct);
        await _uow.SaveChangesAsync(ct);
    }

    public async Task<UsageEventResult> TrackAsync(UsageEventCommand command, CancellationToken ct = default)
    {
        var exists = await _repo.ExistsByIdempotencyKeyAsync(command.IdempotencyKey, ct);

        if (exists)
        {
            return new UsageEventResult
            {
                Success = false,
                Message = "Duplicate event"
            };
        }

        var entity = new UsageEvent
        {
            Id = Guid.NewGuid(),
            WorkspaceId = command.WorkspaceId,

            // FIX 1: string -> enum
            FeatureType = Enum.TryParse<BillingFeatureType>(command.FeatureType, out var feature)
                ? feature
                : throw new InvalidOperationException("Invalid FeatureType"),

            CalculatedCredits = command.Credits,
            IdempotencyKey = command.IdempotencyKey,

            // FIX 2: nullable Guid
            MeetingId = command.MeetingId,

            Status = UsageEventStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        await _repo.AddAsync(entity, ct);
        await _uow.SaveChangesAsync(ct);

        return new UsageEventResult
        {
            Success = true,
            EventId = entity.Id,
            Credits = entity.CalculatedCredits
        };
    }
}