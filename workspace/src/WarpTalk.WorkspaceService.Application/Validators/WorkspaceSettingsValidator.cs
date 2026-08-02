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
    public static WorkspaceSettingsValidationResult Validate(WorkspaceSettingsDto? settings)
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

        if (settings.RequireVerifiedDomainForInternal
            && (settings.VerifiedDomains is null
                || !settings.VerifiedDomains.Any(domain => !string.IsNullOrWhiteSpace(domain))))
        {
            errors["verifiedDomains"] = [WorkspaceConstants.Errors.VerifiedDomainsRequired];
        }

        return new WorkspaceSettingsValidationResult(errors);
    }
}
