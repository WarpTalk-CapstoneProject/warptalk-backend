using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Domain.Constants;

namespace WarpTalk.WorkspaceService.Application.Validators;

public sealed record WorkspaceSettingsValidationResult(
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public string ErrorMessage =>
        Errors.Values.SelectMany(messages => messages).FirstOrDefault()
        ?? WorkspaceConstants.Errors.InvalidSettingsPayload;
}

public static class WorkspaceSettingsValidator
{
    /// <param name="activeVerifiedDomains">
    /// The workspace's live verified domains, read from <c>workspace_verified_domains</c> by the
    /// caller. It is a parameter rather than something read off <paramref name="settings"/>
    /// because <c>settings.VerifiedDomains</c> is a display mirror of that table, not the table:
    /// domains are added and revoked through <c>VerifiedDomainService</c>, which never writes the
    /// settings JSON. Validating against the mirror refused a workspace that had just added a
    /// domain, and accepted one whose only domain had already been revoked — wrong in both
    /// directions.
    /// </param>
    public static WorkspaceSettingsValidationResult Validate(
        WorkspaceSettingsDto? settings,
        IReadOnlyCollection<string> activeVerifiedDomains)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (settings is null)
        {
            errors["settings"] = [WorkspaceConstants.Errors.InvalidSettingsPayload];
            return new WorkspaceSettingsValidationResult(errors);
        }

        if (settings.MaxActiveRooms is < WorkspaceConstants.MinWorkspaceMaxActiveRooms
            or > WorkspaceConstants.MaxWorkspaceMaxActiveRooms)
        {
            errors["maxActiveRooms"] = [WorkspaceConstants.Errors.MaxActiveRoomsOutOfRange];
        }

        if (settings.ArtifactRetentionDays is < WorkspaceConstants.MinWorkspaceArtifactRetentionDays
            or > WorkspaceConstants.MaxWorkspaceArtifactRetentionDays)
        {
            errors["artifactRetentionDays"] = [WorkspaceConstants.Errors.ArtifactRetentionDaysOutOfRange];
        }

        if (settings.InvitationExpiryDays is < WorkspaceConstants.MinWorkspaceInvitationExpiryDays
            or > WorkspaceConstants.MaxWorkspaceInvitationExpiryDays)
        {
            errors["invitationExpiryDays"] = [WorkspaceConstants.Errors.InvitationExpiryDaysOutOfRange];
        }

        if (settings.RequireVerifiedDomainForInternal
            && !activeVerifiedDomains.Any(domain => !string.IsNullOrWhiteSpace(domain)))
        {
            errors["verifiedDomains"] = [WorkspaceConstants.Errors.VerifiedDomainsRequired];
        }

        // WT-646. Only reached when an allowlist is actually configured: null is "no allowlist,
        // defer to AllowAnyPlugins", which is every pre-WT-646 workspace and has nothing to check.
        // An EMPTY list is a configured allowlist permitting nothing — a legitimate, if strict,
        // policy — so it passes.
        //
        // Entries are NOT checked against the plugin catalog. That catalog is AssistantService's
        // table in AssistantService's database; this service has no read path to it and should not
        // grow one on the settings-save path, where an assistant outage would then block saving
        // unrelated workspace settings. A key matching no plugin is accepted and simply matches
        // nothing at enforcement time. This is a known gap, not an oversight: the settings UI is
        // where a real catalog check belongs, since it can already list the catalog.
        if (settings.AllowedPluginKeys is { } allowedPluginKeys)
        {
            if (allowedPluginKeys.Count > WorkspaceConstants.MaxWorkspaceAllowedPluginKeys)
            {
                errors["allowedPluginKeys"] = [WorkspaceConstants.Errors.AllowedPluginKeysTooMany];
            }
            else if (allowedPluginKeys.Any(string.IsNullOrWhiteSpace))
            {
                // Refused rather than filtered out. A blank entry means the caller's payload is
                // not what they think it is, and dropping it would hand back a "saved" allowlist
                // quietly different from the one they sent.
                errors["allowedPluginKeys"] = [WorkspaceConstants.Errors.AllowedPluginKeyBlank];
            }
        }

        return new WorkspaceSettingsValidationResult(errors);
    }
}
