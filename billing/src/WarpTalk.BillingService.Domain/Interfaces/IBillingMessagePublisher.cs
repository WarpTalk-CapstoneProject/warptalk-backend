using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IBillingMessagePublisher
{
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default);
}
