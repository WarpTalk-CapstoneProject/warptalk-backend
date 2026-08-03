using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Models;
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
