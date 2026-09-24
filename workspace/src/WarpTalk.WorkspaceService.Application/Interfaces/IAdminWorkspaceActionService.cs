using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// The workspace-service actions of the admin workspace page: transfer ownership, send the owner a
/// notice, internal notes, the timeline, and the data-summary export. Lifecycle (suspend,
/// reactivate, delete) stays on <see cref="IAdminWorkspaceService"/>.
/// </summary>
public interface IAdminWorkspaceActionService
{
    Task<Result<AdminWorkspaceDetailDto>> TransferOwnershipAsync(
        Guid workspaceId, AdminTransferOwnershipRequest request, Guid actorId, string? correlationId, CancellationToken ct = default);

    Task<Result<AdminWorkspaceNoticeResultDto>> SendNoticeAsync(
        Guid workspaceId, AdminWorkspaceNoticeRequest request, Guid actorId, string? correlationId, CancellationToken ct = default);

    Task<Result<AdminWorkspaceNoteDto>> AddNoteAsync(
        Guid workspaceId, AdminAddWorkspaceNoteRequest request, Guid actorId, string? correlationId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<AdminWorkspaceTimelineEntryDto>>> GetTimelineAsync(
        Guid workspaceId, int? limit, CancellationToken ct = default);

    Task<Result<AdminWorkspaceExportDto>> ExportAsync(
        Guid workspaceId, AdminWorkspaceExportRequest request, Guid actorId, string? correlationId, CancellationToken ct = default);
}
