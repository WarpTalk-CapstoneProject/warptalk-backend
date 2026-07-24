using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface INotificationClient
{
    Task<Result> SendNotificationsAsync(SendBillingNotificationsRequest request, CancellationToken cancellationToken = default);
}
