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
    public ITranslationRoomArtifactRepository TranslationRoomArtifactRepository { get; }

    public UnitOfWork(
        TranslationRoomDbContext context,
        ITranslationRoomRepository translationRoomRepository,
        ITranslationRoomParticipantRepository translationRoomParticipantRepository,
        ITranslationRoomAudioRouteRepository translationRoomAudioRouteRepository,
        ILanguageRepository languageRepository,
        IUserSettingsRepository userSettingsRepository,
        ITranslationRoomArtifactRepository translationRoomArtifactRepository)
    {
        _context = context;
        TranslationRoomRepository = translationRoomRepository;
        TranslationRoomParticipantRepository = translationRoomParticipantRepository;
        TranslationRoomAudioRouteRepository = translationRoomAudioRouteRepository;
        LanguageRepository = languageRepository;
        UserSettingsRepository = userSettingsRepository;
        TranslationRoomArtifactRepository = translationRoomArtifactRepository;
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

    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _currentTransaction;

    public async Task<int> SaveChangesAsync(CancellationToken ct = default) => await _context.SaveChangesAsync(ct);

    public async Task BeginTransactionAsync(CancellationToken ct = default)
    {
        _currentTransaction = await _context.Database.BeginTransactionAsync(ct);
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.CommitAsync(ct);
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.RollbackAsync(ct);
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public void Dispose()
    {
        _currentTransaction?.Dispose();
        _context.Dispose();
    }
}
