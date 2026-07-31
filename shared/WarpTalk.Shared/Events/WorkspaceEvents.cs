using System.Text.Json.Serialization;

namespace WarpTalk.Shared.Events;

public static class WorkspaceEventTypes
{
    public const string Producer = "workspace-service";
    public const string WorkspaceCreated = "workspace.created";
    public const string WorkspaceDeleted = "workspace.deleted";
    public const string MemberRemoved = "workspace.member_removed";
    public const string MemberRoleChanged = "workspace.member_role_changed";
    public const string DocumentIngestionRequested = "workspace.document_ingestion_requested";
    public const string DocumentInvalidated = "workspace.document_invalidated";
}

public sealed record WorkspaceCreatedEventPayload(
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("owner_user_id")] string OwnerUserId,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);

public sealed record WorkspaceDeletedEventPayload(
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("deleted_by_user_id")] string DeletedByUserId,
    [property: JsonPropertyName("deleted_at")] DateTime DeletedAt,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record MemberRemovedEventPayload(
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("removed_by_user_id")] string RemovedByUserId,
    [property: JsonPropertyName("removed_at")] DateTime RemovedAt);

public sealed record MemberRoleChangedEventPayload(
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("target_user_id")] string TargetUserId,
    [property: JsonPropertyName("old_role")] string OldRole,
    [property: JsonPropertyName("new_role")] string NewRole,
    [property: JsonPropertyName("changed_by_user_id")] string ChangedByUserId,
    [property: JsonPropertyName("membership_type")] string? MembershipType,
    [property: JsonPropertyName("effective_behavior")] string? EffectiveBehavior,
    [property: JsonPropertyName("effective_at")] DateTime EffectiveAt,
    [property: JsonPropertyName("idempotency_key")] string? IdempotencyKey);

public sealed record WorkspaceDocumentIngestionRequestedEventPayload(
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("storage_key")] string StorageKey,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("file_extension")] string FileExtension,
    [property: JsonPropertyName("requested_by_user_id")] string RequestedByUserId,
    [property: JsonPropertyName("is_sensitive")] bool IsSensitive);

public sealed record WorkspaceDocumentInvalidatedEventPayload(
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("reason")] string Reason);
