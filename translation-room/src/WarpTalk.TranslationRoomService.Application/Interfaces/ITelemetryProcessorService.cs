using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITelemetryProcessorService
{
    Task<Result> ProcessTelemetryAsync(TelemetryPayload payload, CancellationToken ct = default);
}
