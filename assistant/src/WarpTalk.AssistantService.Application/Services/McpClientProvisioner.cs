using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <summary>
/// Gets a <c>kind='mcp'</c> plugin row ready to start an OAuth flow: discover the authorization
/// server, walk the registration ladder, persist what was learned.
/// </summary>
/// <remarks>
/// This exists so <see cref="PluginConnectionService"/> keeps doing one job. Connect and callback
/// both need a provisioned row, the sequence is three steps with two different failure shapes, and
/// inlining it twice is how the two paths drift apart.
/// <para>
/// A <c>native</c> plugin is returned untouched: it has a hand-written OAuth client reading
/// credentials from configuration and never walks the ladder.
/// </para>
/// </remarks>
public class McpClientProvisioner : IMcpClientProvisioner
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMcpAuthorizationServerDiscovery _discovery;
    private readonly IMcpClientRegistrationResolver _registrationResolver;

    public McpClientProvisioner(
        IUnitOfWork unitOfWork,
        IMcpAuthorizationServerDiscovery discovery,
        IMcpClientRegistrationResolver registrationResolver)
    {
        _unitOfWork = unitOfWork;
        _discovery = discovery;
        _registrationResolver = registrationResolver;
    }

    public async Task<Result<McpClientContextDto>> ProvisionAsync(Plugin plugin, CancellationToken ct = default)
    {
        if (!string.Equals(plugin.Kind, PluginConstants.PluginKind.Mcp, StringComparison.Ordinal))
            return Result.Success(McpClientContextDto.NotApplicable);

        var discovered = await _discovery.DiscoverAsync(plugin, ct);
        if (!discovered.IsSuccess)
        {
            return Result.Failure<McpClientContextDto>(
                discovered.Error ?? "Authorization server discovery failed.",
                discovered.ErrorCode ?? PluginConstants.ErrorCodes.ProviderUnavailable);
        }

        var discovery = discovered.Value!;
        var identity = await _registrationResolver.ResolveAsync(plugin, discovery.AuthorizationServer, ct);

        switch (identity.Outcome)
        {
            case McpClientRegistrationOutcome.Unsupported:
                // Actionable by an operator - register an app with the provider and supply the
                // client id - so it must travel as a structured error the UI can turn into a card,
                // never as an exception.
                return Result.Failure<McpClientContextDto>(
                    identity.Detail ?? "No client registration mechanism applies to this server.",
                    PluginConstants.ErrorCodes.ClientRegistrationUnsupported);

            case McpClientRegistrationOutcome.ProviderUnavailable:
                return Result.Failure<McpClientContextDto>(
                    identity.Detail ?? "The authorization server could not be reached.",
                    PluginConstants.ErrorCodes.ProviderUnavailable);
        }

        McpClientRegistrationMapper.ApplyDiscovery(plugin, discovery);
        McpClientRegistrationMapper.ApplyClientIdentity(plugin, identity);
        _unitOfWork.PluginRepository.Update(plugin);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new McpClientContextDto(true, discovery, identity));
    }
}
