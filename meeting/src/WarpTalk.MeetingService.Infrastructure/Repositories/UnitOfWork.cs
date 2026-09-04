using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly MeetingDbContext _context;
    private IMeetingRoomRepository? _meetingRoomRepository;
    private IRtcStreamParticipantRepository? _meetingParticipantRepository;
    private IMeetingTrackRepository? _meetingTrackRepository;
    private IMeetingChatMessageRepository? _meetingChatMessageRepository;
    private IMeetingChatTranslationRepository? _meetingChatTranslationRepository;
    private IMeetingChatAssistantRequestRepository? _meetingChatAssistantRequestRepository;
    private IMeetingChatModerationEventRepository? _meetingChatModerationEventRepository;
    private IRtcSessionRevocationRepository? _meetingInvitationRepository;

    public UnitOfWork(MeetingDbContext context)
    {
        _context = context;
    }

    public IMeetingRoomRepository MeetingRoomRepository => _meetingRoomRepository ??= new MeetingRoomRepository(_context);
    public IRtcStreamParticipantRepository RtcStreamParticipantRepository => _meetingParticipantRepository ??= new RtcStreamParticipantRepository(_context);
    public IMeetingTrackRepository MeetingTrackRepository => _meetingTrackRepository ??= new MeetingTrackRepository(_context);
    public IMeetingChatMessageRepository MeetingChatMessageRepository => _meetingChatMessageRepository ??= new MeetingChatMessageRepository(_context);
    public IMeetingChatTranslationRepository MeetingChatTranslationRepository => _meetingChatTranslationRepository ??= new MeetingChatTranslationRepository(_context);
    public IMeetingChatAssistantRequestRepository MeetingChatAssistantRequestRepository => _meetingChatAssistantRequestRepository ??= new MeetingChatAssistantRequestRepository(_context);
    public IMeetingChatModerationEventRepository MeetingChatModerationEventRepository => _meetingChatModerationEventRepository ??= new MeetingChatModerationEventRepository(_context);

    public IRtcSessionRevocationRepository RtcSessionRevocationRepository => _meetingInvitationRepository ??= new RtcSessionRevocationRepository(_context);

    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _currentTransaction;

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);

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
        GC.SuppressFinalize(this);
    }
}
