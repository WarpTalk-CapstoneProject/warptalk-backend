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

        // Absent is valid, an unrecognised value is not. The two are different requests: a client
        // that omits the field is asking for whatever the workspace already has, while one that
        // sends "secret" or "VN-ND30" is asking for a posture or a house style this product does
        // not have, and quietly rewriting that to the default would tell the caller its choice was
        // saved. Both settings end up printed on a document with legal weight, so the failure has
        // to be visible at the moment of saving rather than discovered on an exported biên bản.
        //
        // Ordinal, case-sensitive: see the note in WorkspaceConstants on why one spelling.
        if (!string.IsNullOrWhiteSpace(settings.MinutesClassification)
            && !WorkspaceConstants.AllowedMinutesClassifications.Contains(settings.MinutesClassification, StringComparer.Ordinal))
        {
            errors["minutesClassification"] = [WorkspaceConstants.Errors.MinutesClassificationInvalid];
        }

        if (!string.IsNullOrWhiteSpace(settings.MinutesTemplate)
            && !WorkspaceConstants.AllowedMinutesTemplates.Contains(settings.MinutesTemplate, StringComparer.Ordinal))
        {
            errors["minutesTemplate"] = [WorkspaceConstants.Errors.MinutesTemplateInvalid];
        }

        return new WorkspaceSettingsValidationResult(errors);
    }
}
