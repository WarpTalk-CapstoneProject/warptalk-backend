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
    IBreakoutSessionRepository BreakoutSessionRepository { get; }
    IBreakoutAssignmentRepository BreakoutAssignmentRepository { get; }
    IPollRepository PollRepository { get; }
    IPollOptionRepository PollOptionRepository { get; }
    IPollVoteRepository PollVoteRepository { get; }
    IQuestionRepository QuestionRepository { get; }
    IQuestionVoteRepository QuestionVoteRepository { get; }
    IMeetingInvitationRepository MeetingInvitationRepository { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
