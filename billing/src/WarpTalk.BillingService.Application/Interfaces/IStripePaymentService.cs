using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IStripePaymentService
{
    Task<Result<string>> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request, CancellationToken cancellationToken = default);
    Task<Result<bool>> CancelSubscriptionAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<(string Status, string FailureReason)>> GetPaymentStatusAsync(string providerTransactionId, CancellationToken cancellationToken = default);
    Task<Result<CheckoutSessionDto>> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
