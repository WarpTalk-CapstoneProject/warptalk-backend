using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IAudioRouteEventProcessorService
{
    Task<Result> ProcessEventAsync(Guid roomId, Guid? routeId, string eventTypeStr, string payloadJson, CancellationToken ct = default);
}
