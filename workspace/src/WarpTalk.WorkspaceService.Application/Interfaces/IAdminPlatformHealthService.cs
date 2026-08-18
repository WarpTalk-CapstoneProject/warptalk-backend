using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IAdminPlatformHealthService
{
    /// <summary>
    /// Reads the current platform health picture. Never throws for an unreachable monitoring
    /// store — that outcome is carried in the response so the caller renders "monitoring is
    /// unavailable" rather than a 500 or a page of zeroes.
    /// </summary>
    Task<AdminPlatformHealthResponse> ReadAsync(CancellationToken ct = default);
}
