using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WarpTalk.WorkspaceService.Application.DTOs.Admin;

// The platform settings console, as the web reads it (src/types/admin-platform-settings.ts).
// camelCase on the wire; enums as lower-case strings.

public sealed record PlatformSettingActorDto(Guid Id, string? Name, string? Email);

/// <summary>One stored value at one scope.</summary>
public sealed record PlatformSettingScopedValueDto(
    string ScopeType,
    string ScopeId,
    JsonElement Value,
    int Version,
    DateTime UpdatedAt,
    Guid? UpdatedBy);

/// <summary>One registry entry with what is stored for it.</summary>
public sealed record PlatformSettingDto(
    string Key,
    string Category,
    string Type,
    string Label,
    string Description,
    string OwningService,
    IReadOnlyList<string> Scopes,
    string? Unit,
    decimal? Min,
    decimal? Max,
    IReadOnlyList<string>? AllowedValues,
    int? MaxLength,
    string? Pattern,
    bool RequiresRestart,
    bool Risky,
    bool Sensitive,
    bool RequiresSecurityPermission,
    JsonElement DefaultValue,
    /// <summary>The platform value; null when not set (the owning service keeps its deploy-time value).</summary>
    JsonElement? Value,
    int Version,
    bool IsSet,
    IReadOnlyList<PlatformSettingScopedValueDto> Overrides,
    DateTime? LastChangedAt,
    PlatformSettingActorDto? LastChangedBy,
    bool CanEdit);

public sealed record PlatformSettingCategoryDto(string Key, int Count, int ChangedCount);

public sealed record PlatformSettingsPublishStatusDto(long Version, DateTime? PublishedAt, bool Healthy, string? Error);

public sealed record PlatformSettingsConsoleDto(
    IReadOnlyList<PlatformSettingCategoryDto> Categories,
    IReadOnlyList<PlatformSettingDto> Settings,
    PlatformSettingsPublishStatusDto Publish,
    bool CanManage,
    bool CanManageSecurity);

public sealed record PlatformSettingChangeDto(
    Guid Id,
    string Key,
    string ScopeType,
    string ScopeId,
    string Action,
    JsonElement? OldValue,
    JsonElement? NewValue,
    int Version,
    string? Reason,
    PlatformSettingActorDto ChangedBy,
    DateTime ChangedAt,
    Guid? RevertOf,
    bool Redacted);

/// <summary>
/// A write. <see cref="ScopeType"/> defaults to platform. <see cref="ExpectedVersion"/> is the version
/// the editor read (0 when nothing was set); a mismatch is 409, so two admins cannot silently
/// overwrite each other.
/// </summary>
public sealed record PlatformSettingWriteRequest(
    JsonElement Value,
    string? ScopeType,
    string? ScopeId,
    int? ExpectedVersion,
    string? Reason);

public sealed record PlatformSettingResetRequest(string? ScopeType, string? ScopeId, int? ExpectedVersion, string? Reason);

public sealed record PlatformSettingRevertRequest(string? Reason);

public sealed record PlatformSettingsExportEntryDto(string Key, string ScopeType, string ScopeId, JsonElement Value);

public sealed record PlatformSettingsExportDto(
    string Format,
    DateTime ExportedAt,
    IReadOnlyList<PlatformSettingsExportEntryDto> Settings,
    IReadOnlyList<string> Excluded);

public sealed record PlatformSettingsImportRequest(
    IReadOnlyList<PlatformSettingsExportEntryDto>? Settings,
    bool DryRun,
    string? Reason);

/// <summary>One line of an import plan. <see cref="Outcome"/>: create | update | unchanged | rejected.</summary>
public sealed record PlatformSettingsImportLineDto(
    string Key,
    string ScopeType,
    string ScopeId,
    string Outcome,
    JsonElement? OldValue,
    JsonElement? NewValue,
    string? Error);

public sealed record PlatformSettingsImportResultDto(
    bool Applied,
    int Changed,
    int Unchanged,
    int Rejected,
    IReadOnlyList<PlatformSettingsImportLineDto> Lines);

// Integrations (settings console → Integrations). Configured yes/no and non-secret details only.

public sealed record IntegrationServiceReportDto(string Service, bool Configured, string? Detail, DateTimeOffset? ReportedAt);

public sealed record IntegrationCheckDto(bool Ok, DateTimeOffset At, long LatencyMs, string? Detail);

/// <summary><see cref="Configured"/>: null when no service reported it; false when any reporter lacks it.</summary>
public sealed record IntegrationStatusDto(
    string Key,
    string Name,
    bool? Configured,
    IReadOnlyList<IntegrationServiceReportDto> Services,
    IntegrationCheckDto? LastCheck,
    bool Testable,
    string? Href);

public sealed record DeployConfigDto(string Key, string Label, string Service, IReadOnlyList<string>? Value, bool Configured);

public sealed record PlatformIntegrationsDto(IReadOnlyList<IntegrationStatusDto> Integrations, IReadOnlyList<DeployConfigDto> DeployConfig);

public sealed record IntegrationTestResultDto(string Key, bool Ok, long LatencyMs, string? Detail, DateTimeOffset CheckedAt);
