using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly MeetingDbContext _context;
    private IMeetingRoomRepository? _meetingRoomRepository;
    private IMeetingParticipantRepository? _meetingParticipantRepository;
    private IMeetingTrackRepository? _meetingTrackRepository;
    private IMeetingChatMessageRepository? _meetingChatMessageRepository;
    private IMeetingChatTranslationRepository? _meetingChatTranslationRepository;
    private IMeetingChatAssistantRequestRepository? _meetingChatAssistantRequestRepository;
    private IMeetingChatModerationEventRepository? _meetingChatModerationEventRepository;
    private Dictionary<Type, object>? _repositories;

    public UnitOfWork(MeetingDbContext context)
    {
        _context = context;
    }

    public IMeetingRoomRepository MeetingRoomRepository => _meetingRoomRepository ??= new MeetingRoomRepository(_context);
    public IMeetingParticipantRepository MeetingParticipantRepository => _meetingParticipantRepository ??= new MeetingParticipantRepository(_context);
    public IMeetingTrackRepository MeetingTrackRepository => _meetingTrackRepository ??= new MeetingTrackRepository(_context);
    public IMeetingChatMessageRepository MeetingChatMessageRepository => _meetingChatMessageRepository ??= new MeetingChatMessageRepository(_context);
    public IMeetingChatTranslationRepository MeetingChatTranslationRepository => _meetingChatTranslationRepository ??= new MeetingChatTranslationRepository(_context);
    public IMeetingChatAssistantRequestRepository MeetingChatAssistantRequestRepository => _meetingChatAssistantRequestRepository ??= new MeetingChatAssistantRequestRepository(_context);
    public IMeetingChatModerationEventRepository MeetingChatModerationEventRepository => _meetingChatModerationEventRepository ??= new MeetingChatModerationEventRepository(_context);

    public IGenericRepository<T> Repository<T>() where T : class
    {
        _repositories ??= new Dictionary<Type, object>();

        var type = typeof(T);

        if (!_repositories.ContainsKey(type))
        {
            var repositoryType = typeof(GenericRepository<>);
            var repositoryInstance = Activator.CreateInstance(repositoryType.MakeGenericType(type), _context);
            _repositories.Add(type, repositoryInstance!);
        }

        return (IGenericRepository<T>)_repositories[type];
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _context.SaveChangesAsync(ct);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
