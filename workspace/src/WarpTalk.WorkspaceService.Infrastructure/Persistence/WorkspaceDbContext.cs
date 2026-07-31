using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Persistence;

public partial class WorkspaceDbContext : DbContext
{
    public WorkspaceDbContext()
    {
    }

    public WorkspaceDbContext(DbContextOptions<WorkspaceDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<OutboxMessage> OutboxMessages { get; set; }

    public virtual DbSet<Workspace> Workspaces { get; set; }

    public virtual DbSet<WorkspaceDocument> WorkspaceDocuments { get; set; }

    public virtual DbSet<WorkspaceDocumentAccessPolicy> WorkspaceDocumentAccessPolicies { get; set; }

    public virtual DbSet<WorkspaceDocumentAudit> WorkspaceDocumentAudits { get; set; }

    public virtual DbSet<WorkspaceInvitation> WorkspaceInvitations { get; set; }

    public virtual DbSet<WorkspaceMember> WorkspaceMembers { get; set; }

    public virtual DbSet<WorkspaceVerifiedDomain> WorkspaceVerifiedDomains { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresEnum("artifact_type", new[] { "TRANSCRIPT_EXPORT", "SUMMARY_EXPORT", "DEBUG_LOG", "OPTIONAL_RECORDING", "AUDIO_SAMPLE" })
            .HasPostgresEnum("consent_status", new[] { "GRANTED", "REVOKED", "EXPIRED" })
            .HasPostgresEnum("job_status", new[] { "QUEUED", "PROCESSING", "COMPLETED", "FAILED", "CANCELLED" })
            .HasPostgresEnum("notification_status", new[] { "PENDING", "SENT", "DELIVERED", "FAILED", "READ" })
            .HasPostgresEnum("participant_status", new[] { "INVITED", "WAITING", "CONNECTED", "DISCONNECTED", "LEFT", "KICKED", "REJECTED" })
            .HasPostgresEnum("room_status", new[] { "SCHEDULED", "WAITING", "IN_PROGRESS", "PAUSED", "ENDED", "CANCELLED", "EXPIRED", "FAILED" })
            .HasPostgresEnum("ticket_status", new[] { "OPEN", "IN_PROGRESS", "RESOLVED", "CLOSED" })
            .HasPostgresExtension("pg_trgm")
            .HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("outbox_messages_pkey");

            entity.ToTable("outbox_messages", "workspace", tb => tb.HasComment("Transactional outbox for durable Workspace domain-event delivery."));

            entity.HasIndex(e => new { e.DeadLetteredAt, e.CreatedAt }, "idx_workspace_outbox_dead_letter").HasFilter("(dead_lettered_at IS NOT NULL)");

            entity.HasIndex(e => new { e.PublishedAt, e.AvailableAt, e.CreatedAt }, "idx_workspace_outbox_dispatch");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.AttemptCount).HasColumnName("attempt_count");
            entity.Property(e => e.AvailableAt).HasColumnName("available_at");
            entity.Property(e => e.CausationId)
                .HasMaxLength(100)
                .HasColumnName("causation_id");
            entity.Property(e => e.CompatibilityEventType)
                .HasMaxLength(100)
                .HasColumnName("compatibility_event_type");
            entity.Property(e => e.CorrelationId)
                .HasMaxLength(100)
                .HasColumnName("correlation_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeadLetteredAt).HasColumnName("dead_lettered_at");
            entity.Property(e => e.EventType)
                .HasMaxLength(150)
                .HasColumnName("event_type");
            entity.Property(e => e.LastError).HasColumnName("last_error");
            entity.Property(e => e.LockedAt).HasColumnName("locked_at");
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at");
            entity.Property(e => e.PayloadJson)
                .HasColumnType("jsonb")
                .HasColumnName("payload_json");
            entity.Property(e => e.Producer)
                .HasMaxLength(100)
                .HasColumnName("producer");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.SchemaVersion)
                .HasDefaultValue(1)
                .HasColumnName("schema_version");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
        });

        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspaces_pkey");

            entity.ToTable("workspaces", "workspace");

            entity.HasIndex(e => e.Slug, "workspaces_slug_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AllowExternalCollaboration).HasColumnName("allow_external_collaboration");
            entity.Property(e => e.AllowSubdomains).HasColumnName("allow_subdomains");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("deleted_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.LogoUrl)
                .HasMaxLength(500)
                .HasColumnName("logo_url");
            entity.Property(e => e.Name)
                .HasMaxLength(150)
                .HasColumnName("name");
            entity.Property(e => e.OwnerId)
                .HasComment("Internal auth user reference.")
                .HasColumnName("owner_id");
            entity.Property(e => e.RequireVerifiedDomainForInternal)
                .HasDefaultValue(true)
                .HasColumnName("require_verified_domain_for_internal");
            entity.Property(e => e.Settings)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("settings");
            entity.Property(e => e.Slug)
                .HasMaxLength(100)
                .HasColumnName("slug");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("updated_by");
        });

        modelBuilder.Entity<WorkspaceDocument>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_documents_pkey");

            entity.ToTable("workspace_documents", "workspace");

            entity.HasIndex(e => new { e.WorkspaceId, e.AiEligible }, "idx_workspace_documents_workspace_ai");

            entity.HasIndex(e => new { e.WorkspaceId, e.ConfidentialityLevel }, "idx_workspace_documents_workspace_confidentiality");

            entity.HasIndex(e => e.WorkspaceId, "idx_workspace_documents_workspace_id");

            entity.HasIndex(e => new { e.WorkspaceId, e.IsAiAllowed }, "idx_workspace_documents_workspace_is_ai_allowed");

            entity.HasIndex(e => new { e.WorkspaceId, e.SourceLanguage }, "idx_workspace_documents_workspace_lang");

            entity.HasIndex(e => new { e.WorkspaceId, e.RetentionState }, "idx_workspace_documents_workspace_retention");

            entity.HasIndex(e => new { e.WorkspaceId, e.Status }, "idx_workspace_documents_workspace_status");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AiEligible)
                .HasDefaultValue(true)
                .HasColumnName("ai_eligible");
            entity.Property(e => e.AiUsagePolicy)
                .HasColumnType("jsonb")
                .HasColumnName("ai_usage_policy");
            entity.Property(e => e.BusinessDomain)
                .HasMaxLength(100)
                .HasColumnName("business_domain");
            entity.Property(e => e.ConfidentialityLevel)
                .HasMaxLength(30)
                .HasDefaultValueSql("'public_internal'::character varying")
                .HasColumnName("confidentiality_level");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DetectedLanguage)
                .HasMaxLength(20)
                .HasColumnName("detected_language");
            entity.Property(e => e.DocumentType)
                .HasMaxLength(50)
                .HasColumnName("document_type");
            entity.Property(e => e.FileExtension)
                .HasMaxLength(20)
                .HasColumnName("file_extension");
            entity.Property(e => e.FileName)
                .HasMaxLength(255)
                .HasColumnName("file_name");
            entity.Property(e => e.IndexVersion)
                .HasMaxLength(50)
                .HasColumnName("index_version");
            entity.Property(e => e.IngestionStatus)
                .HasMaxLength(30)
                .HasDefaultValueSql("'pending'::character varying")
                .HasColumnName("ingestion_status");
            entity.Property(e => e.IsAiAllowed)
                .HasDefaultValue(true)
                .HasColumnName("is_ai_allowed");
            entity.Property(e => e.Keywords)
                .HasColumnType("jsonb")
                .HasColumnName("keywords");
            entity.Property(e => e.LastIndexedAt).HasColumnName("last_indexed_at");
            entity.Property(e => e.MimeType)
                .HasMaxLength(100)
                .HasColumnName("mime_type");
            entity.Property(e => e.Name)
                .HasMaxLength(255)
                .HasColumnName("name");
            entity.Property(e => e.OwnerId).HasColumnName("owner_id");
            entity.Property(e => e.RetentionState)
                .HasMaxLength(30)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("retention_state");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(20)
                .HasColumnName("source_language");
            entity.Property(e => e.SourceType)
                .HasMaxLength(50)
                .HasColumnName("source_type");
            entity.Property(e => e.Status)
                .HasMaxLength(30)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.StorageKey)
                .HasMaxLength(500)
                .HasColumnName("storage_key");
            entity.Property(e => e.StorageProvider)
                .HasMaxLength(50)
                .HasColumnName("storage_provider");
            entity.Property(e => e.Summary).HasColumnName("summary");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UploadedBy).HasColumnName("uploaded_by");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.Workspace).WithMany(p => p.WorkspaceDocuments)
                .HasForeignKey(d => d.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("workspace_documents_workspace_id_fkey");
        });

        modelBuilder.Entity<WorkspaceDocumentAccessPolicy>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_document_access_policies_pkey");

            entity.ToTable("workspace_document_access_policies", "workspace");

            entity.HasIndex(e => e.DocumentId, "idx_doc_access_policies_doc_id");

            entity.HasIndex(e => new { e.DocumentId, e.SubjectType, e.SubjectId }, "idx_doc_access_policies_lookup");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.Effect)
                .HasMaxLength(20)
                .HasColumnName("effect");
            entity.Property(e => e.Permission)
                .HasMaxLength(30)
                .HasColumnName("permission");
            entity.Property(e => e.SubjectId).HasColumnName("subject_id");
            entity.Property(e => e.SubjectKey)
                .HasMaxLength(150)
                .HasColumnName("subject_key");
            entity.Property(e => e.SubjectType)
                .HasMaxLength(30)
                .HasColumnName("subject_type");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.Document).WithMany(p => p.WorkspaceDocumentAccessPolicies)
                .HasForeignKey(d => d.DocumentId)
                .HasConstraintName("workspace_document_access_policies_document_id_fkey");

            entity.HasOne(d => d.Workspace).WithMany(p => p.WorkspaceDocumentAccessPolicies)
                .HasForeignKey(d => d.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("workspace_document_access_policies_workspace_id_fkey");
        });

        modelBuilder.Entity<WorkspaceDocumentAudit>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_document_audits_pkey");

            entity.ToTable("workspace_document_audits", "workspace");

            entity.HasIndex(e => new { e.ActorId, e.ActionAt }, "idx_workspace_doc_audits_actor_action");

            entity.HasIndex(e => e.DocumentId, "idx_workspace_doc_audits_doc_id");

            entity.HasIndex(e => new { e.WorkspaceId, e.ActionAt }, "idx_workspace_doc_audits_workspace_action");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.Action)
                .HasMaxLength(50)
                .HasColumnName("action");
            entity.Property(e => e.ActionAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("action_at");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.IpAddress)
                .HasMaxLength(64)
                .HasColumnName("ip_address");
            entity.Property(e => e.Metadata)
                .HasColumnType("jsonb")
                .HasColumnName("metadata");
            entity.Property(e => e.UserAgent)
                .HasMaxLength(500)
                .HasColumnName("user_agent");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.Document).WithMany(p => p.WorkspaceDocumentAudits)
                .HasForeignKey(d => d.DocumentId)
                .HasConstraintName("workspace_document_audits_document_id_fkey");

            entity.HasOne(d => d.Workspace).WithMany(p => p.WorkspaceDocumentAudits)
                .HasForeignKey(d => d.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("workspace_document_audits_workspace_id_fkey");
        });

        modelBuilder.Entity<WorkspaceInvitation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_invitations_pkey");

            entity.ToTable("workspace_invitations", "workspace");

            entity.HasIndex(e => new { e.RequestedBy, e.Status, e.CreatedAt }, "ix_workspace_invitations_requested_by_status_created_at")
                .IsDescending(false, false, true)
                .HasFilter("(requested_by IS NOT NULL)");

            entity.HasIndex(e => new { e.WorkspaceId, e.Status, e.CreatedAt }, "ix_workspace_invitations_workspace_id_status_created_at").IsDescending(false, false, true);

            entity.HasIndex(e => e.TokenHash, "workspace_invitations_token_hash_key").IsUnique();

            entity.HasIndex(e => new { e.WorkspaceId, e.Email }, "workspace_invitations_workspace_id_email_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AcceptedAt).HasColumnName("accepted_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeliveryStatus)
                .HasMaxLength(50)
                .HasDefaultValueSql("'NotSent'::character varying")
                .HasColumnName("delivery_status");
            entity.Property(e => e.Email)
                .HasMaxLength(320)
                .HasColumnName("email");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.InvitedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("invited_by");
            entity.Property(e => e.LastSentAt).HasColumnName("last_sent_at");
            entity.Property(e => e.MatchedDomainId).HasColumnName("matched_domain_id");
            entity.Property(e => e.MembershipType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'internal'::character varying")
                .HasColumnName("membership_type");
            entity.Property(e => e.ProviderMessageId)
                .HasMaxLength(255)
                .HasColumnName("provider_message_id");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.SentCount).HasColumnName("sent_count");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'pending'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TokenHash)
                .HasMaxLength(255)
                .HasColumnName("token_hash");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.Workspace).WithMany(p => p.WorkspaceInvitations)
                .HasForeignKey(d => d.WorkspaceId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_invitations_workspace_id_fkey");
        });

        modelBuilder.Entity<WorkspaceMember>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_members_pkey");

            entity.ToTable("workspace_members", "workspace");

            entity.HasIndex(e => new { e.WorkspaceId, e.UserId }, "workspace_members_workspace_id_user_id_idx").IsUnique();

            entity.HasIndex(e => new { e.WorkspaceId, e.UserId }, "workspace_members_workspace_id_user_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CanCreateMeetings)
                .HasDefaultValue(true)
                .HasColumnName("can_create_meetings");
            entity.Property(e => e.JoinedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("joined_at");
            entity.Property(e => e.MembershipType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'internal'::character varying")
                .HasColumnName("membership_type");
            entity.Property(e => e.RemovedAt).HasColumnName("removed_at");
            entity.Property(e => e.RemovedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("removed_by");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.Workspace).WithMany(p => p.WorkspaceMembers)
                .HasForeignKey(d => d.WorkspaceId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_members_workspace_id_fkey");
        });

        modelBuilder.Entity<WorkspaceVerifiedDomain>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_verified_domains_pkey");

            entity.ToTable("workspace_verified_domains", "workspace");

            entity.HasIndex(e => e.Domain, "idx_workspace_verified_domains_unique_verified")
                .IsUnique()
                .HasFilter("((status)::text = 'verified'::text)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Domain)
                .HasMaxLength(255)
                .HasColumnName("domain");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'pending'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VerificationMethod)
                .HasMaxLength(50)
                .HasColumnName("verification_method");
            entity.Property(e => e.VerificationToken)
                .HasMaxLength(255)
                .HasColumnName("verification_token");
            entity.Property(e => e.VerifiedAt).HasColumnName("verified_at");
            entity.Property(e => e.VerifiedBy).HasColumnName("verified_by");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.Workspace).WithMany(p => p.WorkspaceVerifiedDomains)
                .HasForeignKey(d => d.WorkspaceId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("workspace_verified_domains_workspace_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
