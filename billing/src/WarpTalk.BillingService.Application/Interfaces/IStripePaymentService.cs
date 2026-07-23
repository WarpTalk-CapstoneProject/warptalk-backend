using System;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IStripePaymentService
{
    Task<string> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request);
    Task<bool> CancelSubscriptionAsync(Guid workspaceId);
    Task<bool> UpdateSubscriptionAsync(Guid workspaceId, decimal newAmount, string currency, string planSlug);
    Task<(string Status, string FailureReason)> GetPaymentStatusAsync(string providerTransactionId);
    Task<bool> RefundPaymentAsync(string providerTransactionId);
    Task<CheckoutSessionDto> GetCheckoutSessionAsync(string sessionId);
}
