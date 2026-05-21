using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IArtifactsFinalizer
{
    Task ProcessRoomFinalizationAsync(Guid roomId, CancellationToken ct = default);
}
