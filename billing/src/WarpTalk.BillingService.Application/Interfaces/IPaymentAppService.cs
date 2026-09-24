using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPaymentAppService
{
    Task<Result<string>> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request);
    Task<Result> ProcessPaymentEventAsync(StripePaymentEventRequest request);

    /// <summary>
    /// WT-699 / TC3906: a Checkout Session expired unpaid. Marks the payment still waiting on it
    /// Expired. Idempotent — a redelivery, or a session nothing was recorded for, is a no-op.
    /// </summary>
    Task<Result> ExpireCheckoutSessionAsync(string stripeSessionId, CancellationToken cancellationToken = default);
    Task<Result<CheckoutSessionDto>> GetCheckoutSessionAsync(string sessionId);
    Task<Result<CheckoutSessionDto>> GetAndProcessCheckoutSessionAsync(string sessionId, Guid userId, bool isSystemAdmin);
}
