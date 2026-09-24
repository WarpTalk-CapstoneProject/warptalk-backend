using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Interfaces;

public interface IAnnouncementService
{
    Task<Result<AdminAnnouncementPageDto>> ListAsync(AdminAnnouncementListQuery query, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> CreateAsync(Guid adminId, UpsertAnnouncementRequest request, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> UpdateAsync(Guid adminId, Guid id, UpsertAnnouncementRequest request, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> PublishAsync(Guid adminId, Guid id, PublishAnnouncementRequest request, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> UnpublishAsync(Guid adminId, Guid id, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> ArchiveAsync(Guid adminId, Guid id, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> DuplicateAsync(Guid adminId, Guid id, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>What this person should see right now: live, meant for them, not dismissed.</summary>
    Task<Result<IReadOnlyList<ViewerAnnouncementDto>>> GetActiveForViewerAsync(Guid userId, CancellationToken ct = default);
    Task<Result> DismissAsync(Guid userId, Guid id, CancellationToken ct = default);
}
