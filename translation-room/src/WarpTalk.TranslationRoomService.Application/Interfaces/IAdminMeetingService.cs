using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// The platform meeting directory, for the System Admin portal.
///
/// READ ONLY, and metadata only. There is no join, no transcript read and no room control here:
/// an administrator can see that a meeting is running and who is in it, and cannot listen to it.
/// If incident support ever needs more than that, it deserves its own audited path rather than a
/// quiet extra field on this one.
/// </summary>
public interface IAdminMeetingService
{
    Task<Result<AdminPagedResult<AdminMeetingSummaryDto>>> GetDirectoryAsync(
        AdminMeetingDirectoryQuery query,
        CancellationToken ct = default);

    Task<Result<AdminMeetingCountsDto>> GetCountsAsync(CancellationToken ct = default);
}
