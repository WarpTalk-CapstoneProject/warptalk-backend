using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Helpers;

public static class ConcurrencyRetryHelper
{
    public static async Task<Result<T>> ExecuteWithConcurrencyRetryAsync<T>(
        IUnitOfWork unitOfWork,
        ILogger logger,
        Guid workspaceId,
        Func<Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        int maxRetries = HelperConstants.Concurrency.DefaultMaxRetries;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (ex.GetType().Name == HelperConstants.Concurrency.ExceptionName)
            {
                logger.LogWarning(ex, HelperConstants.Concurrency.ConcurrencyLogTemplate, workspaceId, attempt, maxRetries);
                if (attempt == maxRetries)
                    return Result.Failure<T>(ApiMessageConstants.ErrorMessages.BillingConcurrencyConflict, ErrorCodes.BillingConcurrencyConflict);

                await Task.Delay(HelperConstants.Concurrency.BaseDelayMilliseconds * attempt, cancellationToken);
                unitOfWork.ClearTracking();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, HelperConstants.Concurrency.ErrorLogTemplate, workspaceId);
                return Result.Failure<T>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
            }
        }
        return Result.Failure<T>(ApiMessageConstants.ErrorMessages.BillingConcurrencyConflict, ErrorCodes.BillingConcurrencyConflict);
    }
}
