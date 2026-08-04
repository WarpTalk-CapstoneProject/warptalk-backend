using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPaymentAppService
{
    Task<Result<string>> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request);
    Task<Result> ProcessPaymentEventAsync(StripePaymentEventRequest request);
    Task<Result<CheckoutSessionDto>> GetCheckoutSessionAsync(string sessionId);
    Task<Result<CheckoutSessionDto>> GetAndProcessCheckoutSessionAsync(string sessionId, Guid userId, bool isSystemAdmin);
}
