using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly TranslationRoomDbContext _context;

    public ITranslationRoomRepository TranslationRoomRepository { get; }
    public ITranslationRoomParticipantRepository TranslationRoomParticipantRepository { get; }
    public ITranslationRoomAudioRouteRepository TranslationRoomAudioRouteRepository { get; }
    public ILanguageRepository LanguageRepository { get; }
    public ITranslationRoomArtifactRepository TranslationRoomArtifactRepository { get; }
    public ITranslationRoomSessionRepository TranslationRoomSessionRepository { get; }
    public ITranslationRoomInvitationRepository TranslationRoomInvitationRepository { get; }
    public ITranslationRoomFeedbackRepository TranslationRoomFeedbackRepository { get; }
    public ITranslationRoomSeriesRepository TranslationRoomSeriesRepository { get; }

    public UnitOfWork(
        TranslationRoomDbContext context,
        ITranslationRoomRepository translationRoomRepository,
        ITranslationRoomParticipantRepository translationRoomParticipantRepository,
        ITranslationRoomAudioRouteRepository translationRoomAudioRouteRepository,
        ILanguageRepository languageRepository,
        ITranslationRoomArtifactRepository translationRoomArtifactRepository,
        ITranslationRoomSessionRepository translationRoomSessionRepository,
        ITranslationRoomInvitationRepository translationRoomInvitationRepository,
        ITranslationRoomFeedbackRepository translationRoomFeedbackRepository,
        ITranslationRoomSeriesRepository translationRoomSeriesRepository)
    {
        _context = context;
        TranslationRoomRepository = translationRoomRepository;
        TranslationRoomParticipantRepository = translationRoomParticipantRepository;
        TranslationRoomAudioRouteRepository = translationRoomAudioRouteRepository;
        LanguageRepository = languageRepository;
        TranslationRoomArtifactRepository = translationRoomArtifactRepository;
        TranslationRoomSessionRepository = translationRoomSessionRepository;
        TranslationRoomInvitationRepository = translationRoomInvitationRepository;
        TranslationRoomFeedbackRepository = translationRoomFeedbackRepository;
        TranslationRoomSeriesRepository = translationRoomSeriesRepository;
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
