using System.Web;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Exceptions;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

public class PluginConnectionService : IPluginConnectionService, IPluginTokenRefresher
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPluginProviderResolver _providerResolver;
    private readonly IPluginOAuthStateProtector _stateProtector;
    private readonly IPluginCredentialProtector _credentialProtector;
    private readonly ILogger<PluginConnectionService> _logger;
    private readonly IMcpClientProvisioner _mcpClientProvisioner;
    private readonly IWorkspacePluginGuard _workspacePluginGuard;

    public PluginConnectionService(
        IUnitOfWork unitOfWork,
        IPluginProviderResolver providerResolver,
        IPluginOAuthStateProtector stateProtector,
        IPluginCredentialProtector credentialProtector,
        ILogger<PluginConnectionService> logger,
        IMcpClientProvisioner mcpClientProvisioner,
        IWorkspacePluginGuard workspacePluginGuard)
    {
        _unitOfWork = unitOfWork;
        _providerResolver = providerResolver;
        _stateProtector = stateProtector;
        _credentialProtector = credentialProtector;
        _logger = logger;
        _mcpClientProvisioner = mcpClientProvisioner;
        _workspacePluginGuard = workspacePluginGuard;
    }

    /// <summary>
    /// The OAuth client that serves this plugin. Resolved per call rather than injected, because a
    /// single service instance handles plugins of different kinds within one request.
    /// </summary>
    private IPluginOAuthClient OAuthClientFor(Plugin plugin) =>
        _providerResolver.ResolveOAuthClient(plugin.Kind);

    public async Task<Result<PluginConnectUrlDto>> GetConnectUrlAsync(
        string pluginKey,
        Guid userId,
        string? client = null,
        Guid? workspaceId = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? alsoConnect = null)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginConnectUrlDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        // WT-646. This, and not the callback below, is where a connect is refused: it is the last
        // point at which nothing irreversible has happened, and the only one of the two that has a
        // workspace to judge against. Installation is already gated, so this catches the case the
        // install gate cannot - a plugin installed while the workspace still permitted plugins,
        // in a workspace that has since turned them off.
        var permitted = await _workspacePluginGuard.CanUsePluginAsync(workspaceId, userId, plugin, ct);
        if (!permitted.IsSuccess)
            return Result.Failure<PluginConnectUrlDto>(permitted.Error!, permitted.ErrorCode);

        var installed = await _unitOfWork.PluginInstallationRepository.AnyAsync(
            i => i.UserId == userId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct);

        if (!installed)
            return Result.Failure<PluginConnectUrlDto>("Plugin is not installed for this account.", PluginConstants.ErrorCodes.PluginNotInstalled);

        // GMCAL1001. This route always goes to the provider, so every sibling simply joins the
        // consent - no on-the-spot shortcut here, matching what connect-url has always meant.
        var siblings = await ResolveAlsoConnectAsync(plugin, alsoConnect, userId, workspaceId, ct);
        if (!siblings.IsSuccess)
            return Result.Failure<PluginConnectUrlDto>(siblings.Error!, siblings.ErrorCode);

        var url = await BuildAuthorizationUrlAsync(
            plugin,
            userId,
            client,
            ct,
            consentFor: siblings.Value!.Count == 0 ? null : [plugin, .. siblings.Value!.Select(s => s.Plugin)],
            alsoConnectKeys: siblings.Value!.Select(s => s.Plugin.PluginKey).ToList());
        return url.IsSuccess
            ? Result.Success(new PluginConnectUrlDto(url.Value!))
            : Result.Failure<PluginConnectUrlDto>(url.Error!, url.ErrorCode);
    }

    public async Task<Result<PluginConnectResultDto>> ConnectAsync(
        string pluginKey,
        Guid userId,
        string? client = null,
        Guid? workspaceId = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? alsoConnect = null)
    {
        // The same three gates as GetConnectUrlAsync, in the same order, so the two entry points
        // cannot refuse different things.
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginConnectResultDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        var permitted = await _workspacePluginGuard.CanUsePluginAsync(workspaceId, userId, plugin, ct);
        if (!permitted.IsSuccess)
            return Result.Failure<PluginConnectResultDto>(permitted.Error!, permitted.ErrorCode);

        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);

        if (installation == null)
            return Result.Failure<PluginConnectResultDto>("Plugin is not installed for this account.", PluginConstants.ErrorCodes.PluginNotInstalled);

        // GMCAL1001. Validated before anything is decided, so a bad list refuses the whole request
        // rather than connecting half of it. Empty when the caller asked for nothing extra, and then
        // everything below runs exactly as it did before the opt-in existed.
        var siblings = await ResolveAlsoConnectAsync(plugin, alsoConnect, userId, workspaceId, ct);
        if (!siblings.IsSuccess)
            return Result.Failure<PluginConnectResultDto>(siblings.Error!, siblings.ErrorCode);
        if (siblings.Value!.Count > 0)
            return await ConnectWithSiblingsAsync(plugin, installation, siblings.Value!, userId, client, ct);

        // Nothing to redirect to: an API key is pasted on the plugins page, never typed at a provider.
        if (plugin.OAuthClientSource == PluginConstants.OAuthClientSource.ApiKey)
            return Result.Success(new PluginConnectResultDto(false, null, ApiKeyRequired: true));

        // Connected on the spot when the provider's grant already covers this plugin - Meet after
        // Calendar asks Google for nothing Calendar did not already get, and sending the user back
        // through consent for it is the round trip keying the grant by provider exists to avoid.
        //
        // Only for a plugin that is not connected yet. Asking to connect a connected plugin is a
        // reconnect - to refresh an MCP tool list, or to widen a grant the user narrowed at the
        // provider - and that has to reach the provider.
        if (installation.ConnectedAt is null)
        {
            var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
                c => c.UserId == userId && c.Provider == plugin.Provider, ct: ct);

            if (connection is { Status: PluginConstants.ConnectionStatus.Connected }
                && Satisfies(plugin, PluginScopeMapper.FromJson(connection.ScopesJson).ToHashSet(StringComparer.Ordinal)))
            {
                var now = DateTime.UtcNow;
                installation.ConnectedAt = now;
                _unitOfWork.PluginInstallationRepository.Update(installation);
                // WT-710: this path never passes through the callback, which was the only place the
                // tool list was ever fetched. A plugin connected this way stayed at zero tools until
                // somebody reconnected it through the provider.
                await SyncToolManifestAsync(plugin, connection, now, ct);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Success(new PluginConnectResultDto(true, null));
            }
        }

        var url = await BuildAuthorizationUrlAsync(plugin, userId, client, ct);
        return url.IsSuccess
            ? Result.Success(new PluginConnectResultDto(false, url.Value))
            : Result.Failure<PluginConnectResultDto>(url.Error!, url.ErrorCode);
    }

    /// <summary>
    /// GMCAL1001. Most sibling plugins one <c>alsoConnect</c> may name. A provider has a handful of
    /// rows (Google has three); this only bounds a hostile body and the queries it would cost.
    /// </summary>
    public const int MaxAlsoConnect = 10;

    /// <summary>A sibling the user opted to connect alongside the clicked plugin, already gated.</summary>
    private sealed record SiblingTarget(Plugin Plugin, PluginInstallation Installation);

    /// <summary>
    /// GMCAL1001. Turns the caller's <c>alsoConnect</c> keys into gated siblings, or refuses.
    /// </summary>
    /// <remarks>
    /// Each key passes the same gates the clicked plugin did - active, permitted by the workspace,
    /// installed - plus the ones that make it a sibling at all: same provider, so one grant can carry
    /// it, and not an API-key row, which has no consent to join. One bad key refuses the whole
    /// request: connecting the valid half would act on a choice the user did not make as stated.
    /// Duplicates collapse silently; naming the clicked plugin itself is refused as a malformed list.
    /// </remarks>
    private async Task<Result<IReadOnlyList<SiblingTarget>>> ResolveAlsoConnectAsync(
        Plugin plugin,
        IReadOnlyList<string>? alsoConnect,
        Guid userId,
        Guid? workspaceId,
        CancellationToken ct)
    {
        if (alsoConnect is not { Count: > 0 })
            return Result.Success<IReadOnlyList<SiblingTarget>>(Array.Empty<SiblingTarget>());

        if (alsoConnect.Any(string.IsNullOrWhiteSpace))
            return InvalidAlsoConnect("alsoConnect contains a blank plugin key.");

        var keys = alsoConnect.Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count > MaxAlsoConnect)
            return InvalidAlsoConnect($"alsoConnect may name at most {MaxAlsoConnect} plugins.");

        var targets = new List<SiblingTarget>(keys.Count);
        foreach (var key in keys)
        {
            if (string.Equals(key, plugin.PluginKey, StringComparison.Ordinal))
                return InvalidAlsoConnect($"alsoConnect must not name the plugin being connected ('{key}').");

            var sibling = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == key && p.IsActive, ct: ct);
            if (sibling == null)
                return Result.Failure<IReadOnlyList<SiblingTarget>>(
                    $"Unknown plugin '{key}' in alsoConnect.", PluginConstants.ErrorCodes.UnknownPlugin);

            if (!string.Equals(sibling.Provider, plugin.Provider, StringComparison.Ordinal))
                return InvalidAlsoConnect($"'{key}' is not from the same provider as '{plugin.PluginKey}'.");

            if (sibling.OAuthClientSource == PluginConstants.OAuthClientSource.ApiKey)
                return InvalidAlsoConnect($"'{key}' connects with an API key, not by signing in.");

            var permitted = await _workspacePluginGuard.CanUsePluginAsync(workspaceId, userId, sibling, ct);
            if (!permitted.IsSuccess)
                return Result.Failure<IReadOnlyList<SiblingTarget>>(permitted.Error!, permitted.ErrorCode);

            var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
                i => i.UserId == userId
                    && i.PluginId == sibling.Id
                    && i.Status == PluginConstants.InstallationStatus.Installed,
                ct: ct);
            if (installation == null)
                return Result.Failure<IReadOnlyList<SiblingTarget>>(
                    $"Plugin '{key}' is not installed for this account.", PluginConstants.ErrorCodes.PluginNotInstalled);

            targets.Add(new SiblingTarget(sibling, installation));
        }

        return Result.Success<IReadOnlyList<SiblingTarget>>(targets);

        static Result<IReadOnlyList<SiblingTarget>> InvalidAlsoConnect(string error) =>
            Result.Failure<IReadOnlyList<SiblingTarget>>(error, PluginConstants.ErrorCodes.InvalidAlsoConnect);
    }

    /// <summary>
    /// GMCAL1001. <see cref="ConnectAsync"/> when the user opted to bring siblings along.
    /// </summary>
    /// <remarks>
    /// The single-plugin decision, made per plugin. A plugin the live grant already covers is
    /// connected on the spot; everything else shares ONE consent asking for the union of exactly
    /// those plugins' scopes - never a scope of a plugin the user did not name. The clicked plugin
    /// keeps its own rule: already connected means reconnect, so it always joins the consent.
    /// A sibling that is already connected has nothing left to do and is left alone.
    /// <para>
    /// The URL is built before anything is stamped, so a provisioning failure refuses the request
    /// without having connected part of it.
    /// </para>
    /// </remarks>
    private async Task<Result<PluginConnectResultDto>> ConnectWithSiblingsAsync(
        Plugin plugin,
        PluginInstallation installation,
        IReadOnlyList<SiblingTarget> siblings,
        Guid userId,
        string? client,
        CancellationToken ct)
    {
        if (plugin.OAuthClientSource == PluginConstants.OAuthClientSource.ApiKey)
            return Result.Success(new PluginConnectResultDto(false, null, ApiKeyRequired: true));

        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == plugin.Provider, ct: ct);
        var granted = connection is { Status: PluginConstants.ConnectionStatus.Connected }
            ? PluginScopeMapper.FromJson(connection.ScopesJson).ToHashSet(StringComparer.Ordinal)
            : null;

        var onTheSpot = new List<SiblingTarget>();
        var needConsent = new List<Plugin>();
        foreach (var target in siblings.Prepend(new SiblingTarget(plugin, installation)))
        {
            var isMain = ReferenceEquals(target.Plugin, plugin);
            if (target.Installation.ConnectedAt is not null)
            {
                if (isMain) needConsent.Add(target.Plugin);
                continue;
            }

            if (granted != null && Satisfies(target.Plugin, granted))
                onTheSpot.Add(target);
            else
                needConsent.Add(target.Plugin);
        }

        string? url = null;
        if (needConsent.Count > 0)
        {
            var built = await BuildAuthorizationUrlAsync(
                plugin,
                userId,
                client,
                ct,
                consentFor: needConsent,
                alsoConnectKeys: needConsent
                    .Where(p => !ReferenceEquals(p, plugin))
                    .Select(p => p.PluginKey)
                    .ToList());
            if (!built.IsSuccess)
                return Result.Failure<PluginConnectResultDto>(built.Error!, built.ErrorCode);
            url = built.Value;
        }

        if (onTheSpot.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var target in onTheSpot)
            {
                target.Installation.ConnectedAt = now;
                _unitOfWork.PluginInstallationRepository.Update(target.Installation);
                await SyncToolManifestAsync(target.Plugin, connection!, now, ct);
            }
            await _unitOfWork.SaveChangesAsync(ct);
        }

        // Connected speaks for the clicked plugin, as it always has; a URL alongside it means the
        // siblings still need the provider.
        return Result.Success(new PluginConnectResultDto(
            onTheSpot.Any(t => ReferenceEquals(t.Plugin, plugin)),
            url,
            ConnectedPluginKeys: onTheSpot.Count > 0 ? onTheSpot.Select(t => t.Plugin.PluginKey).ToList() : null));
    }

    /// <summary>Longest key accepted. Real API keys are far shorter; this only bounds a hostile body.</summary>
    private const int MaxApiKeyLength = 4096;

    public async Task<Result<PluginCatalogItemDto>> ConnectWithApiKeyAsync(
        string pluginKey,
        Guid userId,
        string? apiKey,
        Guid? workspaceId = null,
        CancellationToken ct = default)
    {
        // The same gates as ConnectAsync, in the same order.
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginCatalogItemDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        var permitted = await _workspacePluginGuard.CanUsePluginAsync(workspaceId, userId, plugin, ct);
        if (!permitted.IsSuccess)
            return Result.Failure<PluginCatalogItemDto>(permitted.Error!, permitted.ErrorCode);

        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);
        if (installation == null)
            return Result.Failure<PluginCatalogItemDto>("Plugin is not installed for this account.", PluginConstants.ErrorCodes.PluginNotInstalled);

        if (plugin.OAuthClientSource != PluginConstants.OAuthClientSource.ApiKey)
            return Result.Failure<PluginCatalogItemDto>(
                $"{plugin.Label} connects by signing in, not with an API key.",
                PluginConstants.ErrorCodes.InvalidApiKey);

        var key = apiKey?.Trim() ?? string.Empty;
        // People paste the header value as often as the key. Either works.
        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = key["Bearer ".Length..].Trim();
        if (key.Length == 0 || key.Length > MaxApiKeyLength)
            return Result.Failure<PluginCatalogItemDto>("Paste your API key.", PluginConstants.ErrorCodes.InvalidApiKey);

        var protectedKey = _credentialProtector.Protect(key);
        var definition = PluginDefinitionMapper.ToDefinition(plugin);

        // Verified before anything is stored: the key is proven by asking the server for its tools
        // with it. A wrong key then fails here, in the dialog where it was typed, instead of on the
        // first tool call minutes later - and a right one arrives with its tool list already synced.
        IReadOnlyList<McpToolDescriptorDto> tools;
        try
        {
            tools = await _providerResolver.ResolveGateway(plugin.Kind).ListToolsAsync(
                definition,
                new PluginConnection
                {
                    UserId = userId,
                    Provider = plugin.Provider,
                    PluginId = plugin.Id,
                    Status = PluginConstants.ConnectionStatus.Connected,
                    EncryptedAccessToken = protectedKey,
                },
                ct);
        }
        catch (PluginProviderException e) when (e.ErrorCode is PluginConstants.ErrorCodes.ConnectionRequired or PluginConstants.ErrorCodes.MissingScope)
        {
            return Result.Failure<PluginCatalogItemDto>(
                $"{plugin.Label} did not accept that API key. Check that it is complete and still active.",
                PluginConstants.ErrorCodes.InvalidApiKey);
        }
        catch (PluginProviderException e)
        {
            _logger.LogInformation(e, "Verifying an API key for plugin {PluginKey} failed.", plugin.PluginKey);
            return Result.Failure<PluginCatalogItemDto>(
                $"{plugin.Label} could not be reached to check the key. Try again in a moment.",
                e.ErrorCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation(e, "MCP server for plugin {PluginKey} was unreachable while verifying an API key.", plugin.PluginKey);
            return Result.Failure<PluginCatalogItemDto>(
                $"{plugin.Label} could not be reached to check the key. Try again in a moment.",
                PluginConstants.ErrorCodes.ProviderUnavailable);
        }

        var now = DateTime.UtcNow;
        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == plugin.Provider, ct: ct);
        if (connection == null)
        {
            connection = new PluginConnection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Provider = plugin.Provider,
                PluginId = plugin.Id,
                CreatedAt = now,
            };
            await _unitOfWork.PluginConnectionRepository.AddAsync(connection, ct);
        }
        else
        {
            _unitOfWork.PluginConnectionRepository.Update(connection);
        }

        // Stored exactly where an OAuth access token would be, so the gateway, the orchestrator and
        // Disconnect need no second path. No refresh token and no expiry: the key lasts until the
        // user revokes it at the provider, and the first 401 after that marks the connection expired.
        connection.Status = PluginConstants.ConnectionStatus.Connected;
        connection.EncryptedAccessToken = protectedKey;
        connection.EncryptedRefreshToken = null;
        connection.AccessTokenExpiresAt = null;
        connection.ScopesJson = "[]";
        connection.ProviderAccountId = null;
        connection.ProviderEmail = null;
        connection.TokenRotatedAt = now;
        connection.UpdatedAt = now;

        installation.ConnectedAt = now;
        _unitOfWork.PluginInstallationRepository.Update(installation);

        plugin.ToolsJson = JsonSerializer.Serialize(tools);
        plugin.ToolsSyncedAt = now;
        plugin.UpdatedAt = now;
        _unitOfWork.PluginRepository.Update(plugin);

        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(PluginCatalogItemMapper.ToCatalogItem(
            PluginDefinitionMapper.ToDefinition(plugin),
            installation,
            connection));
    }

    /// <param name="consentFor">
    /// GMCAL1001. The plugins whose scopes this one consent asks for, when that is not simply
    /// <paramref name="plugin"/> - the opt-in sibling connect. Null keeps the request to the
    /// clicked plugin's own scopes, which is every connect that did not opt in.
    /// </param>
    /// <param name="alsoConnectKeys">The sibling keys sealed into the state for the callback.</param>
    private async Task<Result<string>> BuildAuthorizationUrlAsync(
        Plugin plugin,
        Guid userId,
        string? client,
        CancellationToken ct,
        IReadOnlyList<Plugin>? consentFor = null,
        IReadOnlyList<string>? alsoConnectKeys = null)
    {
        // For an MCP-backed row this is where discovery runs and the registration ladder settles
        // on a client identity, because everything the authorization URL needs - endpoints, client
        // id, negotiated auth method - comes out of it. A native row passes straight through.
        var provisioned = await _mcpClientProvisioner.ProvisionAsync(plugin, ct);
        if (!provisioned.IsSuccess)
            return Result.Failure<string>(provisioned.Error!, provisioned.ErrorCode);

        // WT-710: MCP Authorization's scope selection (challenge, then scopes_supported) on top of
        // whatever the row declares. A native row has no discovery and asks for its declared set.
        var requiredScopes = consentFor is null
            ? PluginScopeMapper.FromJson(plugin.RequiredScopesJson)
            : consentFor
                .SelectMany(p => PluginScopeMapper.FromJson(p.RequiredScopesJson))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        var scopes = McpScopeSelection.Select(requiredScopes, provisioned.Value?.Discovery);
        var oauthClient = OAuthClientFor(plugin);

        // Prepare, then seal, then build: the provider produces the secrets that must round-trip
        // (a PKCE verifier), those go inside the sealed state, and only then can a URL carrying
        // that state be assembled. The requested scopes ride along too, for a token response that
        // omits scope because it granted exactly those.
        var flowState = oauthClient.PrepareState(
            plugin,
            new PluginOAuthStateDto(
                userId,
                plugin.PluginKey,
                Client: PluginConstants.OAuthClient.Normalize(client),
                RequestedScopes: scopes,
                AlsoConnect: alsoConnectKeys is { Count: > 0 } ? alsoConnectKeys : null));
        var state = _stateProtector.Protect(flowState);
        return Result.Success(oauthClient.BuildAuthorizationUrl(plugin, scopes, state, flowState));
    }

    public async Task<PluginOAuthCallbackOutcomeDto> CompleteOAuthCallbackAsync(
        string pluginKey,
        string code,
        string state,
        CancellationToken ct = default)
    {
        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess)
            return Failed(unprotected.ErrorCode!);

        var oauthState = unprotected.Value!;

        // A per-plugin callback path carries the key twice, so the two must agree: a mismatch means
        // the state does not belong to the URL it arrived on.
        if (!string.Equals(oauthState.PluginKey, pluginKey, StringComparison.Ordinal))
            return Failed(PluginConstants.ErrorCodes.PermissionDenied, oauthState.Client);

        // Retired rows are allowed here and nowhere else. The only state that can arrive on this
        // path is one minted before the split, which names google_workspace - a row 20260907100000
        // set is_active=false. Filtering it out would refuse a consent we ourselves started while
        // the row was live. Nothing new can enter through the gap: GetConnectUrlAsync still refuses
        // an inactive row, so a retired plugin can finish a flow but can never begin one.
        return await CompleteCallbackAsync(
            oauthState,
            code,
            ct,
            route: PluginOAuthCallbackRoute.LegacyPerPlugin,
            includeRetiredPlugin: true);
    }

    /// <inheritdoc />
    public async Task<PluginOAuthCallbackOutcomeDto> CompleteProviderOAuthCallbackAsync(
        string provider,
        string code,
        string state,
        CancellationToken ct = default)
    {
        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess)
            return Failed(unprotected.ErrorCode!);

        // The path names a provider, not a plugin, so the plugin key comes from the sealed state -
        // the same arrangement the MCP callback uses. What the path still contributes is a
        // cross-check: the plugin the state names has to belong to the provider whose callback
        // this is, or a state minted for one provider could be redeemed on another's.
        return await CompleteCallbackAsync(unprotected.Value!, code, ct, expectedProvider: provider);
    }

    public async Task<PluginOAuthCallbackOutcomeDto> CompleteMcpOAuthCallbackAsync(
        string code,
        string state,
        string? issuer = null,
        CancellationToken ct = default)
    {
        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess)
            return Failed(unprotected.ErrorCode!);

        // No key in the path to cross-check against - the protected state is the only source, which
        // is exactly why it is integrity-protected rather than merely opaque.
        var oauthState = unprotected.Value!;

        var issuerCheck = ValidateIssuer(oauthState, issuer);
        if (!issuerCheck.IsSuccess)
            return Failed(issuerCheck.ErrorCode!, oauthState.Client);

        return await CompleteCallbackAsync(oauthState, code, ct);
    }

    /// <summary>
    /// RFC 9207: an <c>iss</c> that came back must match the issuer recorded before the redirect.
    /// </summary>
    /// <remarks>
    /// Compared by simple string equality, deliberately: RFC 3986 normalisation - case folding,
    /// default-port elision, trailing slashes - is exactly what an attacker would exploit to make
    /// a different issuer compare equal.
    /// <para>
    /// An absent <c>iss</c> is allowed through, because a server that does not implement RFC 9207
    /// is common and refusing it would break every such provider. The check is a ratchet: it
    /// protects against a response claiming to be from somewhere else, not against silence.
    /// </para>
    /// </remarks>
    private static Result ValidateIssuer(PluginOAuthStateDto oauthState, string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer)) return Result.Success();
        if (string.IsNullOrWhiteSpace(oauthState.Issuer)) return Result.Success();

        return string.Equals(issuer, oauthState.Issuer, StringComparison.Ordinal)
            ? Result.Success()
            : Result.Failure(
                "The authorization response came from a different issuer than the one this flow started with.",
                PluginConstants.ErrorCodes.PermissionDenied);
    }

    /// <inheritdoc />
    public PluginOAuthFlowHintDto? ReadFlowHint(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;

        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess) return null;

        var oauthState = unprotected.Value!;
        return new PluginOAuthFlowHintDto(
            oauthState.PluginKey,
            PluginConstants.OAuthClient.Normalize(oauthState.Client));
    }

    /// <summary>
    /// State is attacker-reachable input: it comes back through the user's browser. Unprotecting it
    /// is the trust boundary, so failures collapse to one indistinguishable error rather than
    /// telling a prober which part it got wrong.
    /// </summary>
    private Result<PluginOAuthStateDto> UnprotectState(string state)
    {
        try
        {
            return Result.Success(_stateProtector.Unprotect(HttpUtility.UrlDecode(state)));
        }
        catch
        {
            return Result.Failure<PluginOAuthStateDto>("Invalid OAuth state.", PluginConstants.ErrorCodes.PermissionDenied);
        }
    }

    /// <summary>
    /// The shared tail of every callback: redeem the code, store the grant, and say what happened.
    /// </summary>
    /// <remarks>
    /// Everything runs inside one <c>catch</c>. The exchange calls a provider over the network with
    /// credentials read from configuration, and the write that follows touches the database - so an
    /// empty client secret, a provider outage and a failed save all end here. Before this, each of
    /// them escaped as an exception and reached the user as a JSON error page on the API domain,
    /// because a callback's caller is a browser following a redirect and has nowhere to put one.
    /// <para>
    /// The provider's own words go to the log and nowhere else: whatever went wrong - a 429, a 503,
    /// a redirect_uri_mismatch, an empty body - has to reach the user as a page they can act on,
    /// and the redirect they follow must not carry a provider's error text.
    /// </para>
    /// </remarks>
    /// <param name="route">
    /// Which redirect URI the browser came back on. It has to be repeated on the token request, so
    /// this travels all the way down to the OAuth client rather than being decided there.
    /// </param>
    /// <param name="includeRetiredPlugin">
    /// Whether a catalog row with <c>is_active=false</c> may complete this callback. True only on
    /// the legacy path, where the state predates the row's retirement.
    /// </param>
    private async Task<PluginOAuthCallbackOutcomeDto> CompleteCallbackAsync(
        PluginOAuthStateDto oauthState,
        string code,
        CancellationToken ct,
        string? expectedProvider = null,
        PluginOAuthCallbackRoute route = PluginOAuthCallbackRoute.Configured,
        bool includeRetiredPlugin = false)
    {
        var pluginKey = oauthState.PluginKey;
        var client = PluginConstants.OAuthClient.Normalize(oauthState.Client);
        string? provider = null;

        try
        {
            var plugin = includeRetiredPlugin
                ? await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct)
                : await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
            if (plugin == null)
                return Failed(PluginConstants.ErrorCodes.UnknownPlugin, client, pluginKey: pluginKey);

            provider = plugin.Provider;

            // Same shape as the per-plugin route's key cross-check: when the path carries an
            // identity too, it and the state have to agree. Collapses to the same opaque error, so
            // a prober cannot tell a wrong provider from a forged state.
            if (expectedProvider != null
                && !string.Equals(plugin.Provider, expectedProvider, StringComparison.Ordinal))
                return Failed(PluginConstants.ErrorCodes.PermissionDenied, client, provider, pluginKey);

            var installed = await _unitOfWork.PluginInstallationRepository.AnyAsync(
                i => i.UserId == oauthState.UserId
                    && i.PluginId == plugin.Id
                    && i.Status == PluginConstants.InstallationStatus.Installed,
                ct);

            if (!installed)
                return Failed(PluginConstants.ErrorCodes.PluginNotInstalled, client, provider, pluginKey);

            var token = await OAuthClientFor(plugin).ExchangeCodeAsync(plugin, code, oauthState, route, ct);

            // By provider, not by plugin. A user who already consented to Google through Drive and
            // is now connecting Calendar comes back here with the same grant: this has to find that
            // row and widen it, not insert a second one that the (user_id, provider) unique
            // constraint would reject.
            var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
                c => c.UserId == oauthState.UserId && c.Provider == plugin.Provider, ct: ct);

            // One grant backs every plugin of this provider, so consenting with a different account
            // does not add a connection - it overwrites the one Drive, Calendar and Meet are all
            // reading. Left unchecked that is silent: every tile changes email, the previous refresh
            // token is destroyed here but stays live at the provider because nothing revokes it, and
            // from then on the same tools read a different Drive than the user's history implies.
            //
            // Refused rather than merged. The user has to disconnect deliberately, which is also the
            // only path that revokes the grant being replaced.
            if (connection is { ProviderAccountId: not null }
                && !string.IsNullOrWhiteSpace(token.ProviderAccountId)
                && !string.Equals(connection.ProviderAccountId, token.ProviderAccountId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Refused a {Provider} consent for a different account on an existing connection "
                        + "(plugin {PluginKey}, user {UserId}).",
                    plugin.Provider,
                    pluginKey,
                    oauthState.UserId);

                // The refusal comes after the exchange, because the account is only knowable from
                // the token response - so a real grant now exists at the provider for an account we
                // are about to forget. Dropping it would leave the user's second account holding
                // access to WarpTalk that WarpTalk has no record of and no later way to revoke.
                await TryRevokeIssuedTokenAsync(plugin, token, ct);

                return Failed(
                    PluginConstants.ErrorCodes.ProviderAccountMismatch,
                    client,
                    provider,
                    pluginKey);
            }

            var now = DateTime.UtcNow;
            var canReuseStoredRefreshToken = connection is
            {
                Status: PluginConstants.ConnectionStatus.Connected,
                EncryptedRefreshToken: not null
            } && !string.IsNullOrWhiteSpace(connection.EncryptedRefreshToken);

            if (connection == null)
            {
                connection = new PluginConnection
                {
                    Id = Guid.NewGuid(),
                    UserId = oauthState.UserId,
                    // Identity. NOT NULL with no database default, so omitting it here fails the
                    // insert at runtime rather than at compile time.
                    Provider = plugin.Provider,
                    // Provenance: which catalog row sent the user to consent. Set once, on the row
                    // that created the connection, and deliberately not rewritten on a later
                    // reconnect through a sibling plugin - "first obtained through" is the only
                    // thing it claims.
                    PluginId = plugin.Id,
                    CreatedAt = now,
                };
                await _unitOfWork.PluginConnectionRepository.AddAsync(connection, ct);
            }
            else
            {
                _unitOfWork.PluginConnectionRepository.Update(connection);
            }

            connection.ProviderAccountId = token.ProviderAccountId;
            connection.ProviderEmail = token.ProviderEmail;
            connection.ScopesJson = JsonSerializer.Serialize(token.GrantedScopes);
            connection.UpdatedAt = now;

            // WT-710: a remote MCP server owes us no refresh token. Many issue only short-lived access
            // tokens, and refusing those grants made such servers impossible to connect at all. The
            // connection works for as long as its access token does; when that runs out,
            // RefreshAccessTokenAsync finds nothing to refresh with and marks it expired, which is
            // the "connect again" the user would have been told now - only an hour later and after
            // the plugin was actually usable.
            //
            // A native provider keeps the stricter rule. Google issues a refresh token whenever it
            // is asked for offline access, which the client always does, so one missing there means
            // a consent that went wrong - better said at once than an hour later.
            var isMcp = string.Equals(plugin.Kind, PluginConstants.PluginKind.Mcp, StringComparison.Ordinal);
            if (isMcp && string.IsNullOrWhiteSpace(token.RefreshToken) && !canReuseStoredRefreshToken)
            {
                // An expired row's stored refresh token is the one that stopped working. Carrying
                // it forward would make the next refresh fail on a grant we already know is dead.
                connection.EncryptedRefreshToken = null;
            }
            else if (string.IsNullOrWhiteSpace(token.RefreshToken) && !canReuseStoredRefreshToken)
            {
                connection.Status = PluginConstants.ConnectionStatus.Expired;
                connection.EncryptedAccessToken = null;
                connection.EncryptedRefreshToken = null;
                connection.AccessTokenExpiresAt = null;
                connection.TokenRotatedAt = null;
                await _unitOfWork.SaveChangesAsync(ct);

                // A consent that came back without a refresh token leaves nothing to act with
                // later. The row is honest about that (`expired`), and the user is told to connect
                // again rather than shown a success they cannot use.
                return new PluginOAuthCallbackOutcomeDto(
                    PluginConstants.CallbackStatus.Error,
                    PluginConstants.ErrorCodes.ConnectionRequired,
                    provider,
                    pluginKey,
                    client,
                    new PluginConnectionStatusDto(
                        pluginKey,
                        connection.Status,
                        connection.ProviderEmail,
                        token.GrantedScopes));
            }

            connection.Status = PluginConstants.ConnectionStatus.Connected;
            connection.EncryptedAccessToken = _credentialProtector.Protect(token.AccessToken);
            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
                connection.EncryptedRefreshToken = _credentialProtector.Protect(token.RefreshToken);
            connection.AccessTokenExpiresAt = token.AccessTokenExpiresAt;
            connection.TokenRotatedAt = now;

            // The plugin the user consented through is the one that becomes connected. Its
            // siblings share the grant but stay as they were: connecting Calendar is not a choice
            // to connect Meet, even though Meet needs nothing more from Google.
            var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
                i => i.UserId == oauthState.UserId
                    && i.PluginId == plugin.Id
                    && i.Status == PluginConstants.InstallationStatus.Installed,
                ct: ct);
            if (installation != null)
            {
                installation.ConnectedAt = now;
                _unitOfWork.PluginInstallationRepository.Update(installation);
            }

            await SyncToolManifestAsync(plugin, connection, now, ct);

            // GMCAL1001. The one exception to the rule above: siblings the user explicitly named
            // in this consent. Still never inferred from the grant alone.
            await ConnectOptedInSiblingsAsync(plugin, oauthState, connection, token.GrantedScopes, now, ct);

            await _unitOfWork.SaveChangesAsync(ct);

            return new PluginOAuthCallbackOutcomeDto(
                await ScopeOutcomeAsync(plugin, oauthState.UserId, token.GrantedScopes, ct),
                null,
                provider,
                pluginKey,
                client,
                new PluginConnectionStatusDto(
                    pluginKey,
                    connection.Status,
                    connection.ProviderEmail,
                    token.GrantedScopes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = Classify(ex);
            _logger.LogError(
                ex,
                "Plugin OAuth callback could not be completed for {PluginKey} ({Reason}); the user is "
                    + "being sent back to the plugins page.",
                pluginKey,
                reason);
            return Failed(reason, client, provider, pluginKey);
        }
    }

    /// <summary>
    /// Connected, or connected-but-narrower than the plugin asked for.
    /// </summary>
    /// <remarks>
    /// The same subset test the plugins page uses to decide whether a tile counts as connected, so
    /// the redirect and the tile cannot disagree about the grant the user just gave.
    /// </remarks>
    private async Task<string> ScopeOutcomeAsync(
        Plugin plugin,
        Guid userId,
        IReadOnlyList<string> grantedScopes,
        CancellationToken ct)
    {
        var granted = new HashSet<string>(grantedScopes, StringComparer.Ordinal);

        if (!Satisfies(plugin, granted)) return PluginConstants.CallbackStatus.Partial;

        // Asked of every installed plugin on this provider, not only the one the user clicked.
        //
        // The grant is shared and this callback replaces its whole scope set, so consenting through
        // Calendar decides what Drive can do. The provider is asked for the union - Google is sent
        // include_granted_scopes=true - but the user can still clear a previously granted box on
        // the consent screen, and then the narrower set is what comes back and what gets stored.
        //
        // Judging that against the clicked plugin alone reported `connected` for a consent that had
        // just dropped Drive's scope: the Drive tile went on saying Connected, and the user found
        // out when a tool call failed in the middle of an answer.
        //
        // Deliberately NOT solved by unioning the new scopes with the stored ones. The stored set
        // has to describe what the provider will actually honour; widening it here would put the
        // failure back one step, at the provider, with the scope gate saying yes on the way past.
        //
        // Only siblings the user has connected are asked. An installed sibling that was never
        // connected loses nothing when the grant narrows, and holding the outcome to its scopes
        // reported `partial` for a consent that gave the user everything they had asked for.
        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(
            i => i.UserId == userId && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);
        var installedPluginIds = installations
            .Where(i => i.ConnectedAt != null || i.PluginId == plugin.Id)
            .Select(i => i.PluginId)
            .ToHashSet();

        var siblings = await _unitOfWork.PluginRepository.FindAsync(
            p => installedPluginIds.Contains(p.Id)
                && p.Provider == plugin.Provider
                && p.IsActive,
            ct: ct);

        return siblings
            .Where(sibling => installedPluginIds.Contains(sibling.Id))
            .All(sibling => Satisfies(sibling, granted))
            ? PluginConstants.CallbackStatus.Connected
            : PluginConstants.CallbackStatus.Partial;
    }

    /// <summary>
    /// GMCAL1001. Marks connected each <c>alsoConnect</c> sibling sealed into the state whose
    /// required scopes the grant that just came back covers.
    /// </summary>
    /// <remarks>
    /// A sibling whose box the user cleared on the consent screen simply stays unconnected; the
    /// clicked plugin is not failed for it. Judged against the granted set as returned, never
    /// unioned with what was stored before - see <see cref="ScopeOutcomeAsync"/> for why.
    /// <para>
    /// No workspace gate, for the reason <see cref="SyncToolManifestAsync"/> gives: these keys were
    /// gated when the flow started, and the callback has no workspace to judge against. Provider,
    /// activity and installation are re-checked because they can change during the round trip.
    /// </para>
    /// </remarks>
    private async Task ConnectOptedInSiblingsAsync(
        Plugin plugin,
        PluginOAuthStateDto oauthState,
        PluginConnection connection,
        IReadOnlyList<string> grantedScopes,
        DateTime now,
        CancellationToken ct)
    {
        if (oauthState.AlsoConnect is not { Count: > 0 } keys) return;

        var granted = new HashSet<string>(grantedScopes, StringComparer.Ordinal);
        foreach (var key in keys
                     .Where(k => !string.IsNullOrWhiteSpace(k) && !string.Equals(k, plugin.PluginKey, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal)
                     .Take(MaxAlsoConnect))
        {
            var sibling = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == key && p.IsActive, ct: ct);
            if (sibling == null
                || !string.Equals(sibling.Provider, plugin.Provider, StringComparison.Ordinal)
                || sibling.OAuthClientSource == PluginConstants.OAuthClientSource.ApiKey)
                continue;

            if (!Satisfies(sibling, granted))
            {
                _logger.LogInformation(
                    "Plugin {PluginKey} was opted in to a {Provider} consent but its scopes were not granted; "
                        + "it stays unconnected.",
                    key,
                    plugin.Provider);
                continue;
            }

            var siblingInstallation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
                i => i.UserId == oauthState.UserId
                    && i.PluginId == sibling.Id
                    && i.Status == PluginConstants.InstallationStatus.Installed,
                ct: ct);
            if (siblingInstallation == null) continue;

            siblingInstallation.ConnectedAt = now;
            _unitOfWork.PluginInstallationRepository.Update(siblingInstallation);
            await SyncToolManifestAsync(sibling, connection, now, ct);
        }
    }

    private static bool Satisfies(Plugin plugin, HashSet<string> grantedScopes) =>
        PluginScopeMapper.FromJson(plugin.RequiredScopesJson).All(grantedScopes.Contains);

    /// <summary>
    /// Which of the three sentences the user should read.
    /// </summary>
    /// <remarks>
    /// Only <see cref="PluginNotConfiguredException"/> earns the configuration reason, and it exists
    /// for exactly this test. Classifying on <c>InvalidOperationException</c> would have caught the
    /// OAuth clients' own "Google refused the code" as a configuration fault, because that is the
    /// type they throw too - and then told an operator to go set a variable that was already set.
    /// <para>
    /// Everything else degrades to transient. Being wrong that way costs the user a retry; being
    /// wrong the other way tells them to retry something that will fail identically forever.
    /// </para>
    /// </remarks>
    private static string Classify(Exception ex) => ex switch
    {
        PluginNotConfiguredException => PluginConstants.ErrorCodes.ProviderConfiguration,
        _ => PluginConstants.ErrorCodes.ProviderUnavailable,
    };

    private static PluginOAuthCallbackOutcomeDto Failed(
        string reason,
        string? client = null,
        string? provider = null,
        string? pluginKey = null) =>
        new(
            PluginConstants.CallbackStatus.Error,
            reason,
            provider,
            pluginKey,
            PluginConstants.OAuthClient.Normalize(client),
            null);

    /// <summary>
    /// Refreshes the cached tool set for an MCP-backed row, using the connection just established.
    /// </summary>
    /// <remarks>
    /// This is the step that turns a catalog row into working tools: <c>tools_json</c> is authored
    /// by us for a native row but is a cache of <c>tools/list</c> for an MCP one, and nothing else
    /// populates it. Until it runs, a connected plugin shows zero tools.
    /// <para>
    /// A failure here does not fail the connect. The grant is real and stored; the tool list is
    /// recoverable by reconnecting, and throwing away a working connection over a momentarily
    /// unreachable server would be a much worse trade.
    /// </para>
    /// <para>
    /// Deliberately not gated by workspace policy, and this stayed true through WT-646, which
    /// moved the connect-time gate up to <see cref="GetConnectUrlAsync"/>. By the time control
    /// reaches here the user has already consented at the provider and the grant exists; refusing
    /// it would strand a real consent rather than prevent one. There is also no workspace to judge
    /// against - a callback is a browser redirect from the provider and carries no workspace
    /// context, only the protected state. Workspace policy is enforced where a user is actually in
    /// a workspace: the catalog and install paths in <c>PluginInstallationService</c>, the
    /// connect-url path above, and the list and execute paths in <c>McpToolOrchestrator</c>.
    /// </para>
    /// </remarks>
    private async Task SyncToolManifestAsync(
        Plugin plugin,
        PluginConnection connection,
        DateTime now,
        CancellationToken ct)
    {
        if (!string.Equals(plugin.Kind, PluginConstants.PluginKind.Mcp, StringComparison.Ordinal)) return;

        try
        {
            var definition = PluginDefinitionMapper.ToDefinition(plugin);
            var tools = await _providerResolver.ResolveGateway(plugin.Kind)
                .ListToolsAsync(definition, connection, ct);

            plugin.ToolsJson = JsonSerializer.Serialize(tools);
            plugin.ToolsSyncedAt = now;
            plugin.UpdatedAt = now;
            _unitOfWork.PluginRepository.Update(plugin);
        }
        catch (Exception e)
        {
            _logger.LogWarning(
                e,
                "Could not refresh the tool manifest for plugin {PluginKey}; the connection stands and "
                    + "the tool list will refresh on the next reconnect.",
                plugin.PluginKey);
        }
    }

    public async Task<Result<PluginConnectionStatusDto>> GetStatusAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginConnectionStatusDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        // The grant belongs to the provider and is looked up by it, but whether this plugin is
        // connected is the installation's to say. A user who consented through Calendar has a grant
        // that covers Meet; they have not connected Meet, and reporting it connected is how Meet
        // used to switch itself on.
        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);

        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == plugin.Provider, ct: ct);

        if (connection == null || installation?.ConnectedAt is null)
            return Result.Success(new PluginConnectionStatusDto(pluginKey, PluginConstants.ConnectionStatus.NotConnected, null, Array.Empty<string>()));

        return Result.Success(new PluginConnectionStatusDto(
            pluginKey,
            connection.Status,
            connection.ProviderEmail,
            PluginScopeMapper.FromJson(connection.ScopesJson)));
    }

    public async Task<Result> RefreshAccessTokenAsync(
        Plugin plugin,
        PluginConnection connection,
        CancellationToken ct = default)
    {
        // Nothing to refresh with. Permanent by construction - only a new consent stores one.
        if (string.IsNullOrWhiteSpace(connection.EncryptedRefreshToken))
            return await PluginConnectionStateTransitions.MarkExpiredAsync(_unitOfWork, connection, ct);

        string refreshToken;
        try
        {
            refreshToken = _credentialProtector.Unprotect(connection.EncryptedRefreshToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Undecryptable stored material (rotated Data Protection key ring) is as dead as a
            // revoked grant - the only way out is a fresh consent.
            return await PluginConnectionStateTransitions.MarkExpiredAsync(_unitOfWork, connection, ct);
        }

        PluginOAuthRefreshResultDto refresh;
        try
        {
            refresh = await OAuthClientFor(plugin).RefreshAccessTokenAsync(plugin, refreshToken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The client classifies everything it can foresee; an unforeseen fault (a provider
            // contract change, a bug in the client) is not evidence the grant is dead. Ending the
            // connection is the one outcome the user cannot undo without a browser round trip, so
            // an unknown fault degrades to transient rather than to destructive.
            return PluginConnectionRefreshFailures.Transient(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                "The provider could not be reached to refresh access. Try again in a moment.");
        }

        switch (refresh.Outcome)
        {
            case PluginOAuthRefreshOutcome.GrantRejected:
                return await PluginConnectionStateTransitions.MarkExpiredAsync(_unitOfWork, connection, ct);

            case PluginOAuthRefreshOutcome.ProviderRateLimited:
                return PluginConnectionRefreshFailures.Transient(
                    PluginConstants.ErrorCodes.ProviderRateLimited,
                    "The provider is rate limiting this account. Try again in a moment.");

            case PluginOAuthRefreshOutcome.ProviderUnavailable:
                return PluginConnectionRefreshFailures.Transient(
                    PluginConstants.ErrorCodes.ProviderUnavailable,
                    "The provider could not be reached to refresh access. Try again in a moment.");
        }

        var token = refresh.Token;
        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
            return PluginConnectionRefreshFailures.Transient(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                "The provider could not be reached to refresh access. Try again in a moment.");

        var now = DateTime.UtcNow;
        connection.EncryptedAccessToken = _credentialProtector.Protect(token.AccessToken);
        // Google returns a refresh token only on the first consent, so an omitted one means "keep
        // using the stored one", not "the grant lost its refresh token".
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            connection.EncryptedRefreshToken = _credentialProtector.Protect(token.RefreshToken);
        connection.AccessTokenExpiresAt = token.AccessTokenExpiresAt;
        connection.Status = PluginConstants.ConnectionStatus.Connected;
        connection.TokenRotatedAt = now;
        connection.UpdatedAt = now;
        _unitOfWork.PluginConnectionRepository.Update(connection);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> DisconnectAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct);
        if (plugin == null)
            return Result.Failure("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        // Disconnecting is per plugin. Disconnecting Drive disconnects Drive: Calendar and Meet,
        // which the user connected separately, keep working on the grant they share.
        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId && i.PluginId == plugin.Id, ct: ct);
        if (installation?.ConnectedAt != null)
        {
            installation.ConnectedAt = null;
            _unitOfWork.PluginInstallationRepository.Update(installation);
        }

        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == plugin.Provider, ct: ct);

        if (connection == null)
        {
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }

        // The grant itself ends only with the last plugin riding on it. Google revokes per grant,
        // not per token, and offers no incremental de-scope, so revoking while Calendar is still
        // connected would leave a plugin we report as healthy pointing at a dead grant. Once
        // nothing connected is left, keeping it would be holding access nobody is using.
        if (await AnyOtherConnectedPluginOnProviderAsync(plugin, userId, ct))
        {
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }

        await TryRevokeProviderTokenAsync(plugin, connection, ct);

        connection.Status = PluginConstants.ConnectionStatus.Revoked;
        connection.EncryptedAccessToken = null;
        connection.EncryptedRefreshToken = null;
        connection.AccessTokenExpiresAt = null;
        connection.TokenRotatedAt = null;
        connection.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.PluginConnectionRepository.Update(connection);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<bool> AnyOtherConnectedPluginOnProviderAsync(
        Plugin plugin,
        Guid userId,
        CancellationToken ct)
    {
        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(
            i => i.UserId == userId && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);
        var connectedPluginIds = installations
            .Where(i => i.ConnectedAt != null && i.PluginId != plugin.Id)
            .Select(i => i.PluginId)
            .ToHashSet();
        if (connectedPluginIds.Count == 0) return false;

        var siblings = await _unitOfWork.PluginRepository.FindAsync(
            p => connectedPluginIds.Contains(p.Id) && p.Provider == plugin.Provider && p.IsActive,
            ct: ct);
        return siblings.Any(sibling => connectedPluginIds.Contains(sibling.Id) && sibling.Provider == plugin.Provider);
    }

    /// <summary>
    /// Best-effort revocation of a grant we obtained and then declined to keep.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryRevokeProviderTokenAsync"/>, which reads the encrypted tokens off
    /// a stored connection: there is no connection here and there never will be one. The refresh
    /// token is preferred for the same reason it is there - providers revoke the whole grant from
    /// it, where an access token may only drop itself.
    /// <para>
    /// Swallows everything. The user is already being redirected to an error page that tells them
    /// what to do; a provider having a bad minute must not turn that into a 500, and the grant left
    /// behind is the same one they would have been left with before this call existed.
    /// </para>
    /// </remarks>
    private async Task TryRevokeIssuedTokenAsync(
        Plugin plugin,
        PluginOAuthTokenDto token,
        CancellationToken ct)
    {
        var revocable = string.IsNullOrWhiteSpace(token.RefreshToken)
            ? token.AccessToken
            : token.RefreshToken;
        if (string.IsNullOrWhiteSpace(revocable)) return;

        try
        {
            await OAuthClientFor(plugin).RevokeTokenAsync(plugin, revocable, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not revoke the grant from a refused {Provider} consent for plugin {PluginKey}.",
                plugin.Provider,
                plugin.PluginKey);
        }
    }

    private async Task TryRevokeProviderTokenAsync(
        Plugin plugin,
        PluginConnection connection,
        CancellationToken ct)
    {
        // An API key is not an OAuth token. Handing it to a revocation endpoint would send the
        // user's key to an authorization server that never issued it; clearing it is the revoke.
        if (plugin.OAuthClientSource == PluginConstants.OAuthClientSource.ApiKey)
            return;

        var encryptedToken =
            string.IsNullOrWhiteSpace(connection.EncryptedRefreshToken)
                ? connection.EncryptedAccessToken
                : connection.EncryptedRefreshToken;
        if (string.IsNullOrWhiteSpace(encryptedToken))
            return;

        string token;
        try
        {
            token = _credentialProtector.Unprotect(encryptedToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        try
        {
            await OAuthClientFor(plugin).RevokeTokenAsync(plugin, token, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Local disconnect is still authoritative for WarpTalk. Provider revoke is best-effort
            // because a network failure here should not trap the user in a connected local state.
        }
    }
}
