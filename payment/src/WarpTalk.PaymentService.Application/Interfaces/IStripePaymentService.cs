using System;
using System.Threading.Tasks;

namespace WarpTalk.PaymentService.Application.Interfaces;

public interface IStripePaymentService
{
    Task<string> CreateCheckoutSessionAsync(Guid userId, Guid workspaceId, decimal amount, string currency, string paymentType, string planSlug = "", string billingCycle = "");
    Task<bool> CancelSubscriptionAsync(Guid workspaceId);
    Task<bool> UpdateSubscriptionAsync(Guid workspaceId, decimal newAmount, string currency, string newPlanName);
    Task<(string Status, string FailureReason)> GetPaymentStatusAsync(string providerTransactionId);
    Task<bool> RefundPaymentAsync(string providerTransactionId);
}
