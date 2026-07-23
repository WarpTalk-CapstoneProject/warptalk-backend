using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Helpers;

public static class ConcurrencyRetryHelper
{
    private const string ConcurrencyExceptionName = "DbUpdateConcurrencyException";
    private const string ConcurrencyLogTemplate = "Concurrency conflict for WorkspaceId {WorkspaceId}. Attempt {Attempt} of {MaxRetries}";
    private const string ErrorLogTemplate = "Error executing operation for WorkspaceId {WorkspaceId}";

    public static async Task<Result<T>> ExecuteWithConcurrencyRetryAsync<T>(
        IUnitOfWork unitOfWork,
        ILogger logger,
        Guid workspaceId,
        Func<Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (ex.GetType().Name == ConcurrencyExceptionName)
            {
                logger.LogWarning(ex, ConcurrencyLogTemplate, workspaceId, attempt, maxRetries);
                if (attempt == maxRetries) 
                    return Result.Failure<T>(ApiMessageConstants.ErrorMessages.BillingConcurrencyConflict, ErrorCodes.BillingConcurrencyConflict);

                await Task.Delay(50 * attempt, cancellationToken);
                unitOfWork.ClearTracking();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ErrorLogTemplate, workspaceId);
                return Result.Failure<T>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
            }
        }
        return Result.Failure<T>(ApiMessageConstants.ErrorMessages.BillingConcurrencyConflict, ErrorCodes.BillingConcurrencyConflict);
    }
}
