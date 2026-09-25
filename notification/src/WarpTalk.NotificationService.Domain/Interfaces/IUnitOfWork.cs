namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    INotificationMessageRepository NotificationMessageRepository { get; }
    INotificationPreferenceRepository NotificationPreferenceRepository { get; }
    INotificationTemplateRepository NotificationTemplateRepository { get; }
    IPushSubscriptionRepository PushSubscriptionRepository { get; }
    IAdminNotificationRepository AdminNotificationRepository { get; }
    INotificationInboxMessageRepository NotificationInboxMessageRepository { get; }
    IAnnouncementRepository AnnouncementRepository { get; }
    IAnnouncementViewerStateRepository AnnouncementViewerStateRepository { get; }
    IAnnouncementDailyStatRepository AnnouncementDailyStatRepository { get; }
    IAnnouncementAssetRepository AnnouncementAssetRepository { get; }
    IEmailBlockRepository EmailBlockRepository { get; }
    IEmailContentVariantRepository EmailContentVariantRepository { get; }
    IEmailCmsVersionRepository EmailCmsVersionRepository { get; }
    IEmailSampleDataSetRepository EmailSampleDataSetRepository { get; }
    IEmailDeliveryStatRepository EmailDeliveryStatRepository { get; }
    IEmailCustomTemplateRepository EmailCustomTemplateRepository { get; }
    IEmailCampaignRepository EmailCampaignRepository { get; }
    IEmailCampaignRecipientRepository EmailCampaignRecipientRepository { get; }
    Task<int> SaveChangesAsync();
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
