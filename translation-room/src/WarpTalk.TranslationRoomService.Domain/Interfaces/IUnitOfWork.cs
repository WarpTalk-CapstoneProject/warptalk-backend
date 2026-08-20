namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    ITranslationRoomRepository TranslationRoomRepository { get; }
    ITranslationRoomParticipantRepository TranslationRoomParticipantRepository { get; }
    ITranslationRoomAudioRouteRepository TranslationRoomAudioRouteRepository { get; }
    ILanguageRepository LanguageRepository { get; }
    ITranslationRoomArtifactRepository TranslationRoomArtifactRepository { get; }
    ITranslationRoomSessionRepository TranslationRoomSessionRepository { get; }
    ITranslationRoomInvitationRepository TranslationRoomInvitationRepository { get; }
    ITranslationRoomFeedbackRepository TranslationRoomFeedbackRepository { get; }
    ITranslationRoomSeriesRepository TranslationRoomSeriesRepository { get; }
    IMeetingMinutesRepository MeetingMinutesRepository { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
