namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// The one lease <see cref="AbandonedRoomSweepWorker"/> and <see cref="IdleRoomMonitoringWorker"/>
/// both take before a tick.
///
/// Both end rooms through EndTranslationRoomAsync, a read-check-write with no concurrency token.
/// Two replicas (or these two workers inside one replica) ending the same room each published
/// RoomEnded, ran session_ends and could queue artifact finalization twice. Sharing one lease
/// serialises every room-ending sweep across the whole deployment.
/// </summary>
public static class RoomEndingSweepLock
{
    public const string Resource = "translation-room:room-ending-sweep";
}
