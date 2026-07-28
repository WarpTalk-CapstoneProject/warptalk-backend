namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    ITranslationRoomRepository TranslationRoomRepository { get; }
    ITranslationRoomParticipantRepository TranslationRoomParticipantRepository { get; }
    ITranslationRoomAudioRouteRepository TranslationRoomAudioRouteRepository { get; }
    ILanguageRepository LanguageRepository { get; }
    ITranslationRoomArtifactRepository TranslationRoomArtifactRepository { get; }
    ITranslationRoomSessionRepository TranslationRoomSessionRepository { get; }
    IGenericRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
