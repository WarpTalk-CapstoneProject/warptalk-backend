using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public class PersistentIdempotencyService : IIdempotencyService
{
    private readonly IUnitOfWork _unitOfWork;

    public PersistentIdempotencyService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<string?> GetResponseJsonAsync(IdempotencyKey key, CancellationToken cancellationToken = default)
    {
        var record = await _unitOfWork.IdempotencyRecords.GetAsync(key.Key, key.Operation, cancellationToken);
        if (record is null || record.ExpiresAt <= DateTime.UtcNow)
            return null;

        if (!string.Equals(record.RequestHash, key.RequestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Idempotency key was reused with a different request payload.");

        return record.ResponseJson;
    }

    public async Task StoreResponseJsonAsync(IdempotencyKey key, string responseJson, Guid? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var existing = await _unitOfWork.IdempotencyRecords.GetAsync(key.Key, key.Operation, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, key.RequestHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Idempotency key was reused with a different request payload.");

            return;
        }

        var record = new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            Key = key.Key,
            Operation = key.Operation,
            WorkspaceId = workspaceId,
            RequestHash = key.RequestHash,
            ResponseJson = responseJson,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        await _unitOfWork.IdempotencyRecords.AddAsync(record, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public static string HashPayload(string payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }
}
