using System;

namespace WarpTalk.Shared.Events;

/// <summary>
/// Event published to consume fractional credits based on resource usage.
/// </summary>
public record ConsumeCreditsEvent
{
    public required string WorkspaceId { get; init; }
    public required string ServiceType { get; init; } // e.g., "AI_SPEECH_TRANSLATION"
    public required int Quantity { get; init; }       // e.g., seconds of speech
    public required string ResourceId { get; init; }  // e.g., MeetingRoomId or TranslationRoomId
    public required DateTime Timestamp { get; init; }
    public string? Notes { get; init; }
}
