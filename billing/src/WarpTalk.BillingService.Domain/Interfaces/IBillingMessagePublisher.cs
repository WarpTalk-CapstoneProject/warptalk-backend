using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IBillingMessagePublisher
{
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default);

    /// <summary>
    /// WT-263: publishes an already-serialized payload. The outbox stores the envelope as JSON, so
    /// the dispatcher must be able to put those exact bytes on the wire; routing them back through
    /// <see cref="PublishAsync{T}"/> would serialize the string a second time and deliver a
    /// JSON-quoted blob instead of an envelope.
    /// </summary>
    Task PublishRawAsync(string topic, string payloadJson, CancellationToken ct = default);
}
