using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class NotificationPreferenceRepository : GenericRepository<NotificationPreference>, INotificationPreferenceRepository
{
    public NotificationPreferenceRepository(NotificationDbContext context) : base(context)
    {
    }
}
