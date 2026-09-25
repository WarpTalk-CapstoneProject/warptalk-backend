using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoomStatus
{
    SCHEDULED,
    WAITING,

    /// <summary>
    /// The booked slot has arrived and the door is open — set by the clock, never by a click
    /// (ScheduledRoomLifecycleWorker). It is deliberately NOT IN_PROGRESS: no translation session
    /// exists, nothing is being billed, and nobody may have arrived yet. The first person who
    /// actually joins takes it to IN_PROGRESS.
    ///
    /// It is also deliberately not WAITING, which the idle (5 min) and abandoned (20 min) sweeps
    /// end when nobody is inside — a room that opens on time and waits for its first participant
    /// would be closed again before they arrived.
    /// </summary>
    OPEN,
    IN_PROGRESS,
    PAUSED,
    ENDED,
    CANCELLED,
    EXPIRED,
    FAILED
}
