using System;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITelemetryStateService
{
    bool IsTelemetryOrTransportEvent(AudioRoutingEventType eventType);
    Task<string> UpdateTransportFlagsAndResolvePayloadAsync(Guid roomId, AudioRoutingEventType eventType);
}
