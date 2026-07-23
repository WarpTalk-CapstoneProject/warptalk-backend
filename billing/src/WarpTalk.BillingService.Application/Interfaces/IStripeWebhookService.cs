using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IStripeWebhookService
{
    Task<bool> HandleWebhookAsync(string jsonPayload, string signatureHeader);
}
