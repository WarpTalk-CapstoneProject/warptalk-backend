using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services.Interface
{
    public interface IMeetingUsageSessionService
    {
        Task<MeetingUsageSession> StartAsync(StartSessionCommand command, CancellationToken ct = default);

        Task StopAsync(Guid sessionId, CancellationToken ct = default);

        Task<MeetingUsageSession?> GetActiveByMeetingIdAsync(Guid meetingId, CancellationToken ct = default);
    }
}
