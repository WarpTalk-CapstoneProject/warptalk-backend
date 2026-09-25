using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IAnnouncementAssetRepository
{
    Task AddAsync(AnnouncementAsset asset, CancellationToken ct = default);

    /// <summary>Untracked, with the bytes.</summary>
    Task<AnnouncementAsset?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
