using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class PersistentIdempotencyService : IIdempotencyService
{
    private readonly IUnitOfWork _unitOfWork;

    public PersistentIdempotencyService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<string?>> GetResponseJsonAsync(IdempotencyKey key, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await _unitOfWork.IdempotencyRecords.GetAsync(key.Key, key.Operation, cancellationToken);
            if (record is null || record.ExpiresAt <= DateTime.UtcNow)
                return Result.Success<string?>(null);

            if (!string.Equals(record.RequestHash, key.RequestHash, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<string?>(BillingMessageConstants.ApiErrorMessages.BillingIdempotencyKeyReused, ErrorCodes.ValidationError);

            return Result.Success<string?>(record.ResponseJson);
        }
        catch (Exception ex)
        {
            return Result.Failure<string?>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> StoreResponseJsonAsync(IdempotencyKey key, string responseJson, Guid? workspaceId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _unitOfWork.IdempotencyRecords.GetAsync(key.Key, key.Operation, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestHash, key.RequestHash, StringComparison.OrdinalIgnoreCase))
                    return Result.Failure(BillingMessageConstants.ApiErrorMessages.BillingIdempotencyKeyReused, ErrorCodes.ValidationError);

                return Result.Success();
            }

            var record = IdempotencyMapper.CreateRecord(key, responseJson, workspaceId);

            await _unitOfWork.IdempotencyRecords.AddAsync(record, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public static string HashPayload(string payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }
}
