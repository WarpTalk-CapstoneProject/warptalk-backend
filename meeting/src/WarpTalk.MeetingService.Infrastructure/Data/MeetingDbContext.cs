using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using WarpTalk.MeetingService.Domain.Entities;

namespace WarpTalk.MeetingService.Infrastructure.Data;

public partial class MeetingDbContext : DbContext
{
    public MeetingDbContext(DbContextOptions<MeetingDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<MeetingChatAssistantRequest> MeetingChatAssistantRequests { get; set; }

    public virtual DbSet<MeetingChatMessage> MeetingChatMessages { get; set; }

    public virtual DbSet<MeetingChatModerationEvent> MeetingChatModerationEvents { get; set; }

    public virtual DbSet<MeetingChatTranslation> MeetingChatTranslations { get; set; }

    public virtual DbSet<MeetingInvitation> MeetingInvitations { get; set; }

    public virtual DbSet<MeetingParticipant> MeetingParticipants { get; set; }

    public virtual DbSet<MeetingRoom> MeetingRooms { get; set; }

    public virtual DbSet<MeetingTrack> MeetingTracks { get; set; }

    public virtual DbSet<SchemaMigration> SchemaMigrations { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<MeetingChatAssistantRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("meeting_chat_assistant_requests_pkey");

            entity.ToTable("meeting_chat_assistant_requests", "meeting");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.ContextScope)
                .HasMaxLength(100)
                .HasDefaultValueSql("'current_meeting'::character varying")
                .HasColumnName("context_scope");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.MeetingRoomId).HasColumnName("meeting_room_id");
            entity.Property(e => e.Prompt).HasColumnName("prompt");
            entity.Property(e => e.RequestedByUserId).HasColumnName("requested_by_user_id");
            entity.Property(e => e.Status)
                .HasMaxLength(50)
                .HasDefaultValueSql("'queued'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TriggerMessageId).HasColumnName("trigger_message_id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.TriggerMessage).WithMany(p => p.MeetingChatAssistantRequests)
                .HasForeignKey(d => d.TriggerMessageId)
                .HasConstraintName("meeting_chat_assistant_requests_trigger_message_id_fkey");
        });

        modelBuilder.Entity<MeetingChatMessage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("meeting_chat_messages_pkey");

            entity.ToTable("meeting_chat_messages", "meeting");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.IsHidden).HasColumnName("is_hidden");
            entity.Property(e => e.MeetingRoomId).HasColumnName("meeting_room_id");
            entity.Property(e => e.Mentions)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("mentions");
            entity.Property(e => e.MessageType)
                .HasMaxLength(50)
                .HasDefaultValueSql("'text'::character varying")
                .HasColumnName("message_type");
            entity.Property(e => e.OriginalLanguage)
                .HasMaxLength(50)
                .HasColumnName("original_language");
            entity.Property(e => e.OriginalText).HasColumnName("original_text");
            entity.Property(e => e.ParticipantId).HasColumnName("participant_id");
            entity.Property(e => e.SenderDisplayName)
                .HasMaxLength(255)
                .HasColumnName("sender_display_name");
            entity.Property(e => e.SenderType)
                .HasMaxLength(50)
                .HasDefaultValueSql("'user'::character varying")
                .HasColumnName("sender_type");
            entity.Property(e => e.SenderUserId).HasColumnName("sender_user_id");
            entity.Property(e => e.TranslationEnabled).HasColumnName("translation_enabled");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.MeetingRoom).WithMany(p => p.MeetingChatMessages)
                .HasForeignKey(d => d.MeetingRoomId)
                .HasConstraintName("meeting_chat_messages_meeting_room_id_fkey");

            entity.HasOne(d => d.Participant).WithMany(p => p.MeetingChatMessages)
                .HasForeignKey(d => d.ParticipantId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("meeting_chat_messages_participant_id_fkey");
        });

        modelBuilder.Entity<MeetingChatModerationEvent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("meeting_chat_moderation_events_pkey");

            entity.ToTable("meeting_chat_moderation_events", "meeting");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Action)
                .HasMaxLength(50)
                .HasColumnName("action");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.MeetingRoomId).HasColumnName("meeting_room_id");
            entity.Property(e => e.MessageId).HasColumnName("message_id");
            entity.Property(e => e.ModeratedByUserId).HasColumnName("moderated_by_user_id");
            entity.Property(e => e.Reason).HasColumnName("reason");

            entity.HasOne(d => d.Message).WithMany(p => p.MeetingChatModerationEvents)
                .HasForeignKey(d => d.MessageId)
                .HasConstraintName("meeting_chat_moderation_events_message_id_fkey");
        });

        modelBuilder.Entity<MeetingChatTranslation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("meeting_chat_translations_pkey");

            entity.ToTable("meeting_chat_translations", "meeting");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Confidence).HasColumnName("confidence");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.MeetingRoomId).HasColumnName("meeting_room_id");
            entity.Property(e => e.MessageId).HasColumnName("message_id");
            entity.Property(e => e.ModelUsed)
                .HasMaxLength(100)
                .HasColumnName("model_used");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(50)
                .HasColumnName("source_language");
            entity.Property(e => e.TargetLanguage)
                .HasMaxLength(50)
                .HasColumnName("target_language");
            entity.Property(e => e.TranslatedText).HasColumnName("translated_text");

            entity.HasOne(d => d.Message).WithMany(p => p.MeetingChatTranslations)
                .HasForeignKey(d => d.MessageId)
                .HasConstraintName("meeting_chat_translations_message_id_fkey");
        });

        modelBuilder.Entity<MeetingInvitation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("meeting_invitations_pkey");

            entity.ToTable("meeting_invitations", "meeting");

            entity.HasIndex(e => e.MeetingRoomId, "idx_meeting_invitations_meeting_room_id");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'PENDING'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.MeetingRoomId).HasColumnName("meeting_room_id");
            entity.Property(e => e.InviteeUserId).HasColumnName("invitee_user_id");
            entity.Property(e => e.InviteeEmail).HasMaxLength(255).HasColumnName("invitee_email");
            entity.Property(e => e.GroupId).HasColumnName("group_id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.MeetingRoom).WithMany(p => p.MeetingInvitations)
                .HasForeignKey(d => d.MeetingRoomId)
                .HasConstraintName("meeting_invitations_meeting_room_id_fkey");
        });

        modelBuilder.Entity<MeetingParticipant>(entity =>
        modelBuilder.Entity<MeetingParticipant>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("meeting_participants_pkey");

            entity.ToTable("meeting_participants", "meeting");

            entity.HasIndex(e => e.MeetingRoomId, "idx_meeting_participants_meeting_room_id");

            entity.HasIndex(e => e.UserId, "idx_meeting_participants_user_id");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.JoinedAt).HasColumnName("joined_at");
            entity.Property(e => e.LeftAt).HasColumnName("left_at");
            entity.Property(e => e.MeetingRoomId).HasColumnName("meeting_room_id");
            entity.Property(e => e.ProviderIdentity)
                .HasMaxLength(255)
                .HasColumnName("provider_identity");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.MeetingRoom).WithMany(p => p.MeetingParticipants)
                .HasForeignKey(d => d.MeetingRoomId)
        modelBuilder.Entity<MeetingRoom>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("meeting_rooms_pkey");

            entity.ToTable("meeting_rooms", "meeting");

            entity.HasIndex(e => e.TranslationRoomId, "idx_meeting_rooms_translation_room_id");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.ProviderRoomName)
                .HasMaxLength(255)
                .HasColumnName("provider_room_name");
            entity.Property(e => e.ActiveHostId).HasColumnName("active_host_id");
            entity.Property(e => e.Status)
                .HasMaxLength(255)
                .HasDefaultValueSql("'CREATED'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<MeetingTrack>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("meeting_tracks_pkey");

            entity.ToTable("meeting_tracks", "meeting");

            entity.HasIndex(e => e.MeetingParticipantId, "idx_meeting_tracks_meeting_participant_id");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.IsMuted).HasColumnName("is_muted");
            entity.Property(e => e.MediaType)
                .HasMaxLength(255)
                .HasDefaultValueSql("'VIDEO'::character varying")
                .HasColumnName("media_type");
            entity.Property(e => e.MeetingParticipantId).HasColumnName("meeting_participant_id");
            entity.Property(e => e.ProviderTrackId)
                .HasMaxLength(255)
                .HasColumnName("provider_track_id");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.UnpublishedAt).HasColumnName("unpublished_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
>>>>>>> development

            entity.HasOne(d => d.MeetingParticipant).WithMany(p => p.MeetingTracks)
                .HasForeignKey(d => d.MeetingParticipantId)
                .HasConstraintName("meeting_tracks_meeting_participant_id_fkey");
        });

        modelBuilder.Entity<SchemaMigration>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("schema_migrations_pkey");

            entity.ToTable("schema_migrations", "meeting");

            entity.HasIndex(e => e.MigrationKey, "schema_migrations_migration_key_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AppliedBy)
                .HasMaxLength(100)
                .HasColumnName("applied_by");
            entity.Property(e => e.Checksum)
                .HasMaxLength(128)
                .HasColumnName("checksum");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.ExecutionTimeMs).HasColumnName("execution_time_ms");
            entity.Property(e => e.MigrationKey)
                .HasMaxLength(150)
                .HasColumnName("migration_key");
            entity.Property(e => e.MigrationName)
                .HasMaxLength(255)
                .HasColumnName("migration_name");
            entity.Property(e => e.ScriptPath)
                .HasMaxLength(500)
                .HasColumnName("script_path");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'success'::character varying")
                .HasColumnName("status");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
