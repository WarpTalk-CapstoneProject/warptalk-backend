using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioRoutingEventType
{
    participants_and_languages_configured,
    session_starts,
    host_ends_session,
    host_pauses_session,
    host_resumes_session,
    stop_routing_and_flush_data,
    transcript_recording_summary_linked
}
