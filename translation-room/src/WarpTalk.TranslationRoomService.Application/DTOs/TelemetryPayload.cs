using System;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public class TelemetryPayload
{
    public Guid RoomId { get; set; }
    public Guid RouteId { get; set; }
    public string WorkerType { get; set; } = null!; // "stt" or "tts"
    public double LatencyMs { get; set; }
    public long Timestamp { get; set; }
}
