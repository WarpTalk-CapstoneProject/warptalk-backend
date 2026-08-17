using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Product feedback across the platform, for the System Admin portal.
///
/// READ ONLY. Ratings cannot be edited or deleted here — a quality signal an administrator can
/// remove is not a quality signal. Comments are returned without the person who wrote them:
/// nothing on the screen this feeds acts on a person, so naming one would only add a record.
/// </summary>
public interface IAdminFeedbackService
{
    Task<Result<AdminFeedbackSummaryDto>> GetSummaryAsync(
        AdminFeedbackQuery query,
        CancellationToken ct = default);

    Task<Result<AdminPagedResult<AdminFeedbackCommentDto>>> GetCommentsAsync(
        AdminFeedbackQuery query,
        CancellationToken ct = default);
}
