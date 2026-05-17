namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    ITranslationRoomRepository TranslationRoomRepository { get; }
    ITranslationRoomParticipantRepository TranslationRoomParticipantRepository { get; }
<<<<<<< HEAD
    ITranslationRoomAudioRouteRepository TranslationRoomAudioRouteRepository { get; }
=======
>>>>>>> 80e45ad1325ea4819c4e38a4a5b6fa5c95549e8d
    ILanguageRepository LanguageRepository { get; }
    IUserSettingsRepository UserSettingsRepository { get; }
    IGenericRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
