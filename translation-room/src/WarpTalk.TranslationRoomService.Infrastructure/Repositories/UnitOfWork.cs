using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly TranslationRoomDbContext _context;
    private readonly Dictionary<Type, object> _repositories = new();

    public ITranslationRoomRepository TranslationRoomRepository { get; }
    public ITranslationRoomParticipantRepository TranslationRoomParticipantRepository { get; }
<<<<<<< HEAD
    public ITranslationRoomAudioRouteRepository TranslationRoomAudioRouteRepository { get; }
=======
>>>>>>> 80e45ad1325ea4819c4e38a4a5b6fa5c95549e8d
    public ILanguageRepository LanguageRepository { get; }
    public IUserSettingsRepository UserSettingsRepository { get; }

    public UnitOfWork(
        TranslationRoomDbContext context, 
        ITranslationRoomRepository translationRoomRepository,
        ITranslationRoomParticipantRepository translationRoomParticipantRepository,
<<<<<<< HEAD
        ITranslationRoomAudioRouteRepository translationRoomAudioRouteRepository,
=======
>>>>>>> 80e45ad1325ea4819c4e38a4a5b6fa5c95549e8d
        ILanguageRepository languageRepository,
        IUserSettingsRepository userSettingsRepository)
    {
        _context = context;
        TranslationRoomRepository = translationRoomRepository;
        TranslationRoomParticipantRepository = translationRoomParticipantRepository;
<<<<<<< HEAD
        TranslationRoomAudioRouteRepository = translationRoomAudioRouteRepository;
=======
>>>>>>> 80e45ad1325ea4819c4e38a4a5b6fa5c95549e8d
        LanguageRepository = languageRepository;
        UserSettingsRepository = userSettingsRepository;
    }

    public IGenericRepository<T> Repository<T>() where T : class
    {
        var type = typeof(T);
        if (!_repositories.ContainsKey(type))
        {
            var repositoryInstance = new GenericRepository<T>(_context);
            _repositories.Add(type, repositoryInstance);
        }
        return (IGenericRepository<T>)_repositories[type];
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default) => await _context.SaveChangesAsync(ct);

    public void Dispose() => _context.Dispose();
}
