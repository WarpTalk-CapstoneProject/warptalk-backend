using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IMessagePublisher
{
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default);
}
