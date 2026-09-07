using System.Collections.Generic;
using WarpTalk.WorkspaceService.Domain.Constants;

namespace WarpTalk.WorkspaceService.Application.DTOs.Workspace;

public record WorkspaceSettingsDto(
    string DefaultLanguage,
    string Timezone,
    List<string> AllowedTargetLanguages,
    bool VoiceCloningEnabled,
    int MaxActiveRooms,
    int ArtifactRetentionDays,
    List<string> VerifiedDomains,
    bool AllowExternalCollaboration,
    bool RequireVerifiedDomainForInternal,
    AiUsagePolicyDto? AiUsagePolicy,
    bool IsProfanityFilterEnabled,
    int InvitationExpiryDays = 7,
    /// <summary>
    /// The most concurrent rooms this workspace's plan permits, whatever
    /// <see cref="MaxActiveRooms"/> says. A workspace may tighten below it and may never raise
    /// above it (EntitlementConstants.Errors.WorkspaceOverrideLoosens), so when this is the
    /// smaller of the two it — not the stored setting — is what meeting creation enforces.
    ///
    /// Reported so the settings page stops presenting a number that is not in force. A workspace
    /// with an inactive subscription resolves every entitlement to the platform default, which is
    /// 5: an owner reading "Max Active Rooms: 20" and being refused at 5 has no way, from that
    /// screen, to discover that their subscription is the reason.
    ///
    /// Null when the workspace has no entitlement snapshot yet (cold start), where no plan quota
    /// is in force at all and the stored setting is the only rule.
    /// </summary>
    int? MaxActiveRoomsCeiling = null,
    /// <summary>Provenance of the ceiling — <c>plan:enterprise</c>, <c>platform_default</c>, … Null with the ceiling.</summary>
    string? MaxActiveRoomsCeilingSource = null,
    bool AllowAnyPlugins = true,
    /// <summary>
    /// How many target languages this workspace's plan permits IN ONE MEETING. WT-500.
    ///
    /// Not a cap on <see cref="AllowedTargetLanguages"/>, and deliberately not turned into one:
    /// the allowlist says which languages a meeting may choose FROM, and the plan says how many it
    /// may choose AT ONCE. A workspace permitting six and running three-language meetings is a
    /// coherent configuration, so clamping the list would take away something nobody was misusing.
    ///
    /// What was wrong is that the quota was invisible until it fired. It is enforced at meeting
    /// creation (WorkspaceDirectoryService.ValidatePlanLanguageQuota), so an owner who enabled six
    /// languages here got no warning at all and then a refusal at the point of creating a meeting —
    /// with nothing on the settings screen connecting the two. That is the same defect
    /// MaxActiveRoomsCeiling above was added to fix, one field across.
    ///
    /// Resolved through <c>Limit</c>, NOT <c>SelfServiceLimit</c>, because that is the function
    /// meeting creation calls. Reporting a ceiling stricter than the one actually applied would be
    /// a new version of the same lie.
    ///
    /// Null when no plan quota is in force — cold start, or no live subscription.
    /// </summary>
    int? MaxLanguagesCeiling = null,
    /// <summary>Provenance of the language ceiling. Null with the ceiling.</summary>
    string? MaxLanguagesCeilingSource = null,
    /// <summary>
    /// The classification new minutes in this workspace open at — <c>Internal</c>,
    /// <c>Confidential</c> or <c>Public</c>. WT-643.
    ///
    /// It exists because the policy block printed on the face of a biên bản had nowhere to read a
    /// classification from, and MeetingMinutesDocxWriter will not print one it cannot back. The
    /// choices were to leave the line off every document, or to let the writer decide — and a
    /// document generator that decides for itself how sensitive a record is has invented the one
    /// fact on the page that governs who may be shown it.
    ///
    /// A workspace-level default, not a per-document label: this is where a workspace states the
    /// posture its meetings start from, and overriding a single document is a separate question
    /// this field deliberately does not answer.
    ///
    /// Defaults to Internal rather than to nothing. Unlike the block's other lines, whose absence
    /// is honest — no signer, so no signer line — a classification the workspace never chose is
    /// still a real posture, and the closed one is the safe direction to be wrong in.
    /// </summary>
    string MinutesClassification = WorkspaceConstants.DefaultMinutesClassification,
    /// <summary>
    /// Which of the two presentation templates this workspace's minutes open in and export as —
    /// <c>vn-nd30</c> (Nghị định 30/2020 conventions) or <c>global-en</c>. WT-643.
    ///
    /// Neither template replaces the other and neither is a translation of the other: they are two
    /// presentations of the same stored record, and the workspace picks which one its documents
    /// are filed as. Storing the choice here rather than per document is what makes a workspace's
    /// minutes look like each other — a filing convention is a property of the organisation, not
    /// of a meeting.
    ///
    /// Defaults to vn-nd30 because that is the document this system already writes. A workspace
    /// that has never opened this setting is not asking for a new house style, so the default has
    /// to be the status quo; defaulting to global-en would have restyled every existing
    /// workspace's minutes the day the setting appeared, without anyone choosing it.
    /// </summary>
    string MinutesTemplate = WorkspaceConstants.DefaultMinutesTemplate
);

public record WorkspaceSettingsPatchRequest(
    string? DefaultLanguage = null,
    string? Timezone = null,
    List<string>? AllowedTargetLanguages = null,
    bool? VoiceCloningEnabled = null,
    int? MaxActiveRooms = null,
    int? ArtifactRetentionDays = null,
    List<string>? VerifiedDomains = null,
    bool? AllowExternalCollaboration = null,
    bool? RequireVerifiedDomainForInternal = null,
    AiUsagePolicyPatchDto? AiUsagePolicy = null,
    bool? IsProfanityFilterEnabled = null,
    bool? AllowAnyPlugins = null,
    /// <summary>Null leaves the stored classification alone; it does not reset it to the default.</summary>
    string? MinutesClassification = null,
    /// <summary>Null leaves the stored template alone; it does not reset it to the default.</summary>
    string? MinutesTemplate = null
);

public record AiUsagePolicyPatchDto(
    bool? AllowExternalLlm = null,
    PiiRedactionDto? RedactPii = null,
    DlpDto? Dlp = null,
    TranslationProfileDto? TranslationProfile = null,
    bool? UseGlobalGlossary = null
);
