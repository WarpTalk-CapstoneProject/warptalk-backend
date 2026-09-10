using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.BackgroundProcessors;

public class ArtifactsFinalizationQueue : IArtifactsFinalizationQueue
{
    private readonly Channel<FinalizationRequest> _channel;

    public ArtifactsFinalizationQueue()
    {
        // Unbounded channel for safety, or bounded with capacity if we expect huge traffic spikes
        _channel = Channel.CreateUnbounded<FinalizationRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void QueueFinalization(Guid roomId, string? templateKey = null, string? summaryLanguage = null)
    {
        _channel.Writer.TryWrite(new FinalizationRequest(roomId, templateKey, summaryLanguage));
    }

    public IAsyncEnumerable<FinalizationRequest> ReadAllAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}
