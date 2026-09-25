using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

/// <summary>
/// The v1 email template store (one row per email, English only). Superseded by
/// EmailContentVariant in the CMS v2 migration, which copied every active row across; kept only so
/// the table stays mapped.
/// </summary>
public interface INotificationTemplateRepository : IGenericRepository<NotificationTemplate>
{
}
