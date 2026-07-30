using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITelemetryProcessor
{
    Task ProcessTelemetryAsync(TelemetryPayload payload, CancellationToken ct = default);
}
