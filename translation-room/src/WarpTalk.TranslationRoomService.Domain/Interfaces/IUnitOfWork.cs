namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    ITranslationRoomRepository TranslationRoomRepository { get; }
    ITranslationRoomParticipantRepository TranslationRoomParticipantRepository { get; }
    ITranslationRoomAudioRouteRepository TranslationRoomAudioRouteRepository { get; }
    ILanguageRepository LanguageRepository { get; }
    IUserSettingsRepository UserSettingsRepository { get; }
    IGenericRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
