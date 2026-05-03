using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services.Interface
{
    public interface ISubscriptionService
    {
        Task<Subscription> GetActiveAsync(Guid workspaceId, CancellationToken ct = default);

        Task<Subscription> CreateAsync(CreateSubscriptionCommand command, CancellationToken ct = default);

        Task UpgradeAsync(UpgradeSubscriptionCommand command, CancellationToken ct = default);

        Task CancelAsync(Guid subscriptionId, CancellationToken ct = default);
    }
}
