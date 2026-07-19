using System.Threading.Tasks;
using WarpTalk.PaymentService.Application.DTOs;

namespace WarpTalk.PaymentService.Application.Interfaces;

public interface IPaymentAppService
{
    Task<string> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request);
    Task ProcessPaymentEventAsync(string stripeSessionId, string paymentIntentId, decimal amount, string currency, string userId, string workspaceId, string paymentType, string status, string failureReason = "", string invoiceUrl = "", string invoicePdf = "", string planSlug = "", string billingCycle = "");
}
