using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;

public class MeetingUsageSessionService : IMeetingUsageSessionService
{
    private readonly IMeetingUsageSessionRepository _repo;
    private readonly IUnitOfWork _uow;

    public MeetingUsageSessionService(
        IMeetingUsageSessionRepository repo,
        IUnitOfWork uow)
    {
        _repo = repo;
        _uow = uow;
    }

    public async Task AddAsync(MeetingUsageSession entity, CancellationToken ct = default)
    {
        await _repo.AddAsync(entity, ct);
        await _uow.SaveChangesAsync(ct);
    }

    // ===================================================
    // GET ACTIVE SESSION
    // ===================================================
    public async Task<MeetingUsageSession?> GetActiveByMeetingIdAsync(
        Guid meetingId,
        CancellationToken ct = default)
    {
        return await _repo.GetActiveByMeetingIdAsync(meetingId, ct);
    }

    // ===================================================
    // START SESSION
    // ===================================================
    public async Task<MeetingUsageSession> StartAsync(
        StartSessionCommand command,
        CancellationToken ct = default)
    {
        var session = new MeetingUsageSession
        {
            Id = Guid.NewGuid(),
            MeetingId = command.MeetingId,
            WorkspaceId = command.WorkspaceId,
            HostUserId = command.HostUserId,
            Status = UsageSessionStatus.Active,
            StartedAt = DateTime.UtcNow
        };

        await _repo.AddAsync(session, ct);
        await _uow.SaveChangesAsync(ct);

        return session;
    }

    // ===================================================
    // STOP SESSION
    // ===================================================
    public async Task StopAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct);
        if (session == null) return;

        session.Status = UsageSessionStatus.Ended;
        session.EndedAt = DateTime.UtcNow;

        await _repo.UpdateAsync(session, ct);
        await _uow.SaveChangesAsync(ct);
    }
}