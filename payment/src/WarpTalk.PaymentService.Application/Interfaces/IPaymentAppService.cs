using System.Threading.Tasks;
using WarpTalk.PaymentService.Application.DTOs;

namespace WarpTalk.PaymentService.Application.Interfaces;

public interface IPaymentAppService
{
    Task<string> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request);
    Task ProcessCheckoutSessionCompletedAsync(string stripeSessionId, string paymentIntentId, decimal amount, string currency, string userId, string paymentType);
}
