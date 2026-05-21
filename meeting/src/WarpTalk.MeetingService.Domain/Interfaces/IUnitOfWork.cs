namespace WarpTalk.MeetingService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IMeetingRoomRepository MeetingRoomRepository { get; }
    IMeetingParticipantRepository MeetingParticipantRepository { get; }
    IMeetingTrackRepository MeetingTrackRepository { get; }
    IGenericRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
