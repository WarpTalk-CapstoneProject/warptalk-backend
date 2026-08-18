using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Applies a mid-meeting language change to the participant row and rebuilds the audio mesh. WT-419.
///
/// WHY THIS IS NOT PART OF IAudioRouteEventProcessor
///     That processor moves EXISTING routes through a state machine. A language change does
///     something else: it decides which routes should exist at all. Folding it in would also be a
///     dependency cycle — TranslationRoomAudioRouteService already takes an IAudioRouteEventProcessor,
///     so the processor cannot take the route service back.
///
///     TranslationRoomEventConsumerService resolves whichever of the two an event needs, and is in
///     neither object graph.
/// </summary>
public interface IParticipantLanguageProcessor
{
    /// <summary>
    /// Persist the languages this user just chose and regenerate the room's routes.
    ///
    /// Either language may be null, meaning "unchanged" — the gateway publishes one hub call at a
    /// time and a null must not be read as a request to blank the column.
    /// </summary>
    Task<Result> ProcessLanguageChangeAsync(
        Guid roomId,
        Guid userId,
        string? speakLanguage,
        string? listenLanguage,
        CancellationToken ct = default);
}
