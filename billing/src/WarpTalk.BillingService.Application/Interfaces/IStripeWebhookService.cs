using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IStripeWebhookService
{
    Task<Result<bool>> HandleWebhookAsync(string jsonPayload, string signatureHeader, CancellationToken cancellationToken = default);
}
