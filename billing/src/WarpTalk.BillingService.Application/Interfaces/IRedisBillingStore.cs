using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IRedisBillingStore
{
    Task<Result> SetSessionActiveAsync(Guid sessionId, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<Result<bool>> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<Guid>>> GetExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<Result> RemoveSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<Result> PushTempUsageLogDtoAsync(TempUsageLogDto log, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TempUsageLogDto>>> GetTempUsageLogBatchAsync(int batchSize, CancellationToken cancellationToken = default);
    Task<Result> TrimTempUsageLogBatchAsync(int processedCount, CancellationToken cancellationToken = default);

    Task<Result> SetAiServiceStateAsync(Guid workspaceId, string serviceState, string? suspendedReason, CancellationToken cancellationToken = default);
    Task<Result> SetAiServiceStateForRoomAsync(Guid translationRoomId, string serviceState, string? suspendedReason, CancellationToken cancellationToken = default);
}
