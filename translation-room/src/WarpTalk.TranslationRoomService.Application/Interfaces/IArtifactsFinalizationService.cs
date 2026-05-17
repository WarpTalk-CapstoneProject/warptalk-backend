using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IArtifactsFinalizationService
{
    Task<Result> FinalizeRoomArtifactsAsync(Guid roomId, CancellationToken ct = default);
    Task ProcessRoomFinalizationAsync(Guid roomId, CancellationToken ct = default);
}
