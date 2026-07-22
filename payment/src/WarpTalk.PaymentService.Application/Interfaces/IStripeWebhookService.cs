using System.Threading.Tasks;

namespace WarpTalk.PaymentService.Application.Interfaces;

public interface IStripeWebhookService
{
    Task<bool> HandleWebhookAsync(string jsonPayload, string signatureHeader);
}
