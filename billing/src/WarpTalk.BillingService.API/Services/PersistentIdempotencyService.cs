using System.Security.Cryptography;
using System.Text;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.API.Services;

public class PersistentIdempotencyService : IIdempotencyService
{
    private readonly IUnitOfWork _unitOfWork;

    public PersistentIdempotencyService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<string?> GetResponseJsonAsync(string key, string operation, string requestHash, CancellationToken ct = default)
    {
        var record = await _unitOfWork.IdempotencyRecords.GetAsync(key, operation, ct);
        if (record is null || record.ExpiresAt <= DateTime.UtcNow)
            return null;

        if (!string.Equals(record.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Idempotency key was reused with a different request payload.");

        return record.ResponseJson;
    }

    public async Task StoreResponseJsonAsync(string key, string operation, string requestHash, string responseJson, Guid? workspaceId = null, CancellationToken ct = default)
    {
        var existing = await _unitOfWork.IdempotencyRecords.GetAsync(key, operation, ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Idempotency key was reused with a different request payload.");

            return;
        }

        var record = new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            Key = key,
            Operation = operation,
            WorkspaceId = workspaceId,
            RequestHash = requestHash,
            ResponseJson = responseJson,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        await _unitOfWork.IdempotencyRecords.AddAsync(record, ct);
        await _unitOfWork.SaveChangesAsync(ct);
    }

    public static string HashPayload(string payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }
}