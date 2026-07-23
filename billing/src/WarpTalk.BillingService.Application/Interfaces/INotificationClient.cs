using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface INotificationClient
{
    Task SendNotificationAsync(Guid userId, string type, string title, string body, string actionUrl, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
}
