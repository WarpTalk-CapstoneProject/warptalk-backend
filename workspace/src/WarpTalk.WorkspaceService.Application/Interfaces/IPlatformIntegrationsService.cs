using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// The Integrations section of the settings console: what every service reports it is configured
/// for (never a value), the last connection check, and a harmless read-only connection test for the
/// integrations this service can reach itself.
/// </summary>
public interface IPlatformIntegrationsService
{
    Task<PlatformIntegrationsDto> GetAsync(CancellationToken ct = default);

    /// <summary>NotFound for an unknown key; ValidationError for one this service cannot test.</summary>
    Task<Result<IntegrationTestResultDto>> TestAsync(string key, CancellationToken ct = default);
}
