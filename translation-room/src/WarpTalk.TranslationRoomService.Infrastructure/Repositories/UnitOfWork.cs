using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly TranslationRoomDbContext _context;
    private readonly Dictionary<Type, object> _repositories = new();

    public ITranslationRoomRepository TranslationRoomRepository { get; }
    public ITranslationRoomParticipantRepository TranslationRoomParticipantRepository { get; }
    public ITranslationRoomAudioRouteRepository TranslationRoomAudioRouteRepository { get; }
    public ILanguageRepository LanguageRepository { get; }
    public IUserSettingsRepository UserSettingsRepository { get; }

    public UnitOfWork(
        TranslationRoomDbContext context, 
        ITranslationRoomRepository translationRoomRepository,
        ITranslationRoomParticipantRepository translationRoomParticipantRepository,
        ITranslationRoomAudioRouteRepository translationRoomAudioRouteRepository,
        ILanguageRepository languageRepository,
        IUserSettingsRepository userSettingsRepository)
    {
        _context = context;
        TranslationRoomRepository = translationRoomRepository;
        TranslationRoomParticipantRepository = translationRoomParticipantRepository;
        TranslationRoomAudioRouteRepository = translationRoomAudioRouteRepository;
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
