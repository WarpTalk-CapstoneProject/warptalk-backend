using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class PushSubscriptionRepository : GenericRepository<PushSubscription>, IPushSubscriptionRepository
{
    public PushSubscriptionRepository(NotificationDbContext context) : base(context)
    {
    }
}
