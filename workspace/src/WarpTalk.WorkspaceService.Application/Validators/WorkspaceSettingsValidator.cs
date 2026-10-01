using System.Globalization;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Helpers;
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

        ValidateLanguagePolicy(settings, errors);

        return new WorkspaceSettingsValidationResult(errors);
    }

    /// <summary>
    /// WT-706. The whitelist used to be stored exactly as it arrived — "vi-VN", "VI", "", a code
    /// no catalogue has ever held, the same language twice. None of that was refused and all of
    /// it had consequences: rooms store primary subtags, so a workspace that saved ["vi-VN"]
    /// could create no meeting at all, with a settings screen that looked right.
    ///
    /// Nothing is checked while the workspace is unrestricted, on purpose. The list is then not
    /// policy — it is cleared on save (WorkspaceMapper.ToConfiguration) — and refusing a stale
    /// entry inside a document the owner is switching to "allow all" would block the very save
    /// that makes the entry irrelevant.
    /// </summary>
    private static void ValidateLanguagePolicy(
        WorkspaceSettingsDto settings,
        Dictionary<string, string[]> errors)
    {
        if (!WorkspaceLanguagePolicy.IsRestricted(settings.RestrictLanguages, settings.AllowedTargetLanguages))
        {
            return;
        }

        var normalized = WorkspaceLanguagePolicy.Normalize(settings.AllowedTargetLanguages);
        if (!normalized.IsValid)
        {
            errors["allowedTargetLanguages"] =
            [
                string.Format(
                    CultureInfo.InvariantCulture,
                    WorkspaceConstants.Errors.AllowedTargetLanguageNotRecognizedFormat,
                    normalized.OffendingCode)
            ];
            return;
        }

        // Reached either by sending restrictLanguages: true with nothing selected, or by a list
        // holding nothing but blanks. Both are an owner asking for a restriction that permits no
        // language whatsoever, which no reader can express — empty means unrestricted everywhere
        // downstream — so it is refused here rather than quietly becoming its own opposite.
        if (normalized.Codes.Count == 0)
        {
            errors["allowedTargetLanguages"] = [WorkspaceConstants.Errors.AllowedTargetLanguagesRequiredWhenRestricted];
            return;
        }

        // The workspace's own default has to be a language the workspace permits. Without this a
        // restricted workspace could default every new meeting to a language its own policy
        // refuses at creation — a setting that can only ever produce an error.
        var defaultLanguage = LanguageTag.Base(settings.DefaultLanguage);
        if (defaultLanguage.Length > 0 && !normalized.Codes.Contains(defaultLanguage))
        {
            errors["defaultLanguage"] =
            [
                string.Format(
                    CultureInfo.InvariantCulture,
                    WorkspaceConstants.Errors.DefaultLanguageNotAllowedFormat,
                    settings.DefaultLanguage)
            ];
        }
    }
}
