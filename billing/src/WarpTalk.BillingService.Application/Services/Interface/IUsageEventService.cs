using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services.Interface
{
    public interface IUsageEventService
    {
        Task<UsageEventResult> TrackAsync(UsageEventCommand command, CancellationToken ct = default);

        Task<IReadOnlyList<UsageEvent>> GetPendingAsync(int take, CancellationToken ct = default);

        Task MarkProcessedAsync(Guid eventId, CancellationToken ct = default);

        Task<bool> ExistsByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    }
}
