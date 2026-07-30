namespace WarpTalk.MeetingService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IMeetingRoomRepository MeetingRoomRepository { get; }
    IMeetingParticipantRepository MeetingParticipantRepository { get; }
    IMeetingTrackRepository MeetingTrackRepository { get; }
    IMeetingChatMessageRepository MeetingChatMessageRepository { get; }
    IMeetingChatTranslationRepository MeetingChatTranslationRepository { get; }
    IMeetingChatAssistantRequestRepository MeetingChatAssistantRequestRepository { get; }
    IMeetingChatModerationEventRepository MeetingChatModerationEventRepository { get; }
    IGenericRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
