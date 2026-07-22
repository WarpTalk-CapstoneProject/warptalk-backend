using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.Application.Services;

public sealed class AssistantAgentJobQueue : IAssistantAgentJobQueue
{
    private readonly Channel<AssistantAgentJob> _channel = Channel.CreateUnbounded<AssistantAgentJob>();

    public ValueTask EnqueueAsync(AssistantAgentJob job, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<AssistantAgentJob> DequeueAllAsync(CancellationToken ct = default)
        => _channel.Reader.ReadAllAsync(ct);
}
