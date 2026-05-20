using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.BackgroundProcessors;

public class ArtifactsFinalizationQueue : IArtifactsFinalizationQueue
{
    private readonly Channel<Guid> _channel;

    public ArtifactsFinalizationQueue()
    {
        // Unbounded channel for safety, or bounded with capacity if we expect huge traffic spikes
        _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void QueueFinalization(Guid roomId)
    {
        _channel.Writer.TryWrite(roomId);
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}
