using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IIdempotencyService
{
    Task<Result<string?>> GetResponseJsonAsync(IdempotencyKey key, CancellationToken cancellationToken = default);

    Task<Result> StoreResponseJsonAsync(
        IdempotencyKey key,
        string responseJson,
        Guid? workspaceId = null,
        CancellationToken cancellationToken = default);
}
