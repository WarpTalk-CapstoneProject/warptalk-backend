using System;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IArtifactsFinalizationQueue
{
    void QueueFinalization(Guid roomId);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}
