using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPaymentAppService
{
    Task<string> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request);
    Task ProcessPaymentEventAsync(StripePaymentEventRequest request);
    Task<CheckoutSessionDto> GetCheckoutSessionAsync(string sessionId);
}
