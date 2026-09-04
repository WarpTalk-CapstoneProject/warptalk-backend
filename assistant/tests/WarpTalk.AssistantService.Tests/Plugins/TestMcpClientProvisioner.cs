using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// Stands in for the client-registration ladder in tests that exercise a <c>native</c> plugin.
/// </summary>
/// <remarks>
/// Returning <see cref="McpClientContextDto.NotApplicable"/> is not a shortcut - it is what the
/// real provisioner does for a plugin that is not <c>kind='mcp'</c>, so these tests keep asserting
/// the same behaviour the production path has. Tests that need a resolved or failed ladder should
/// substitute the interface directly rather than extend this.
/// </remarks>
public class TestMcpClientProvisioner : IMcpClientProvisioner
{
    public Task<Result<McpClientContextDto>> ProvisionAsync(Plugin plugin, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(McpClientContextDto.NotApplicable));
}
