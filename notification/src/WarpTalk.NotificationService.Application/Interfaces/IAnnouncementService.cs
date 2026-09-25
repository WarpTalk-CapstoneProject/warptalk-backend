using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.NotificationService.Application.DTOs.Common;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.NotificationService.Application.Interfaces;

public interface IAnnouncementService
{
    Task<Result<AdminAnnouncementPageDto>> ListAsync(AdminAnnouncementListQuery query, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<AnnouncementAnalyticsDto>> GetAnalyticsAsync(Guid id, int days, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> CreateAsync(AdminActorContext actor, UpsertAnnouncementRequest request, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> UpdateAsync(AdminActorContext actor, Guid id, UpsertAnnouncementRequest request, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> PublishAsync(AdminActorContext actor, Guid id, PublishAnnouncementRequest request, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> UnpublishAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> ArchiveAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);
    Task<Result<AdminAnnouncementDto>> DuplicateAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);
    Task<Result> DeleteAsync(AdminActorContext actor, Guid id, CancellationToken ct = default);
    Task<Result<BulkResultDto>> BulkAsync(AdminActorContext actor, AnnouncementBulkRequest request, CancellationToken ct = default);
    Task<Result<AnnouncementAssetDto>> UploadAssetAsync(AdminActorContext actor, string fileName, string contentType, byte[] content, CancellationToken ct = default);
    Task<Result<AnnouncementAssetContent>> GetAssetAsync(Guid id, CancellationToken ct = default);

    /// <summary>What this person should see right now: live, meant for them, and due under its show frequency.</summary>
    Task<Result<IReadOnlyList<ViewerAnnouncementDto>>> GetActiveForViewerAsync(
        Guid userId, string? locale = null, string? sessionId = null, CancellationToken ct = default);

    /// <summary>An impression, dismissal or click. Best-effort: never fails the viewer for analytics.</summary>
    Task<Result> RecordEventAsync(Guid userId, Guid id, AnnouncementEventRequest request, CancellationToken ct = default);
}
