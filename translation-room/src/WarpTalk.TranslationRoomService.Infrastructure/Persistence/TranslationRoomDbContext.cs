using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Infrastructure.Persistence;

public partial class TranslationRoomDbContext : DbContext
{
    public TranslationRoomDbContext()
    {
    }

    public TranslationRoomDbContext(DbContextOptions<TranslationRoomDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<TranslationRoom> TranslationRooms { get; set; }

    public virtual DbSet<TranslationRoomAudioRoute> TranslationRoomAudioRoutes { get; set; }

    public virtual DbSet<TranslationRoomArtifact> TranslationRoomArtifacts { get; set; }

    public virtual DbSet<TranslationRoomFeedback> TranslationRoomFeedbacks { get; set; }

    public virtual DbSet<TranslationRoomParticipant> TranslationRoomParticipants { get; set; }






    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");

        modelBuilder.Entity<TranslationRoom>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translationRooms_pkey");

            entity.ToTable("translation_rooms", "translation_room");

            entity.HasIndex(e => e.HostId, "idx_translation_rooms_host");

            entity.HasIndex(e => e.ScheduledAt, "idx_translation_rooms_scheduled")
                .IsDescending()
                .HasFilter("(deleted_at IS NULL)");

            entity.HasIndex(e => e.Status, "idx_translation_rooms_status").HasFilter("(deleted_at IS NULL)");

            entity.HasIndex(e => e.WorkspaceId, "idx_translation_rooms_workspace");

            entity.HasIndex(e => e.TranslationRoomCode, "translation_rooms_translation_room_code_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("public.uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.DurationSeconds).HasColumnName("duration_seconds");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
            entity.Property(e => e.HostId).HasColumnName("host_id");
            entity.Property(e => e.MaxParticipants)
                .HasDefaultValue(10)
                .HasColumnName("max_participants");
            entity.Property(e => e.TranslationRoomCode)
                .HasMaxLength(12)
                .HasColumnName("translation_room_code");
            entity.Property(e => e.TranslationRoomType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'GROUP'::character varying")
                .HasColumnName("translation_room_type");
            entity.Property(e => e.ScheduledAt).HasColumnName("scheduled_at");
            entity.Property(e => e.Settings)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("settings");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(5)
                .IsFixedLength()
                .HasColumnName("source_language");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasColumnName("status");
            entity.Property(e => e.TargetLanguages)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("target_languages");
            entity.Property(e => e.Title)
                .HasMaxLength(255)
                .HasColumnName("title");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
        });

        modelBuilder.Entity<TranslationRoomAudioRoute>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_audio_routes_pkey");

            entity.ToTable("translation_room_audio_routes", "translation_room");

            entity.HasIndex(e => e.TranslationRoomId, "idx_audio_routes_translation_room");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("public.uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(5)
                .IsFixedLength()
                .HasColumnName("source_language");
            entity.Property(e => e.SourceParticipantId).HasColumnName("source_participant_id");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.StreamId)
                .HasMaxLength(100)
                .HasColumnName("stream_id");
            entity.Property(e => e.TargetLanguage)
                .HasMaxLength(5)
                .IsFixedLength()
                .HasColumnName("target_language");
            entity.Property(e => e.TargetParticipantId).HasColumnName("target_participant_id");
            entity.Property(e => e.VoiceCloneEnabled).HasColumnName("voice_clone_enabled");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomAudioRoutes)
                .HasForeignKey(d => d.TranslationRoomId)
                .HasConstraintName("translation_room_audio_routes_translation_room_id_fkey");

            entity.HasOne(d => d.SourceParticipant).WithMany(p => p.TranslationRoomAudioRouteSourceParticipants)
                .HasForeignKey(d => d.SourceParticipantId)
                .HasConstraintName("translation_room_audio_routes_source_participant_id_fkey");

            entity.HasOne(d => d.TargetParticipant).WithMany(p => p.TranslationRoomAudioRouteTargetParticipants)
                .HasForeignKey(d => d.TargetParticipantId)
                .HasConstraintName("translation_room_audio_routes_target_participant_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomArtifact>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_artifacts_pkey");

            entity.ToTable("translation_room_artifacts", "translation_room");

            entity.HasIndex(e => e.TranslationRoomId, "idx_artifacts_translation_room");

            entity.HasIndex(e => e.RetentionUntil, "idx_artifacts_retention_until");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("public.uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.FileUrl)
                .HasMaxLength(500)
                .HasColumnName("file_url");
            entity.Property(e => e.FileFormat)
                .HasMaxLength(20)
                .HasColumnName("file_format");
            entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes");
            entity.Property(e => e.ContainsRawAudio).HasColumnName("contains_raw_audio");
            entity.Property(e => e.ContainsRawVideo).HasColumnName("contains_raw_video");
            entity.Property(e => e.ConsentRequired).HasColumnName("consent_required");
            entity.Property(e => e.RetentionUntil).HasColumnName("retention_until");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomArtifacts)
                .HasForeignKey(d => d.TranslationRoomId)
                .HasConstraintName("translation_room_artifacts_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomFeedback>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_feedback_pkey");

            entity.ToTable("translation_room_feedback", "translation_room");

            entity.HasIndex(e => e.TranslationRoomId, "idx_feedback_translation_room");

            entity.HasIndex(e => new { e.TranslationRoomId, e.UserId }, "translation_room_feedback_translation_room_id_user_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("public.uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.AudioQuality).HasColumnName("audio_quality");
            entity.Property(e => e.Comments).HasColumnName("comments");
            entity.Property(e => e.CommunicationInsights)
                .HasColumnType("jsonb")
                .HasColumnName("communication_insights");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.OverallRating).HasColumnName("overall_rating");
            entity.Property(e => e.TranslationQuality).HasColumnName("translation_quality");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.VoiceCloneQuality).HasColumnName("voice_clone_quality");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomFeedbacks)
                .HasForeignKey(d => d.TranslationRoomId)
                .HasConstraintName("translation_room_feedback_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomParticipant>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_participants_pkey");

            entity.ToTable("translation_room_participants", "translation_room");

            entity.HasIndex(e => e.TranslationRoomId, "idx_participants_translation_room");

            entity.HasIndex(e => e.UserId, "idx_participants_user");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("public.uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.ConnectionType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'webrtc'::character varying")
                .HasColumnName("connection_type");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            
            
            entity.Property(e => e.IsTranslationAudioEnabled).HasColumnName("is_translation_audio_enabled");
            
            entity.Property(e => e.IsUsingVoiceClone).HasColumnName("is_using_voice_clone");
            entity.Property(e => e.JoinedAt).HasColumnName("joined_at");
            entity.Property(e => e.LeftAt).HasColumnName("left_at");
            entity.Property(e => e.ListenLanguage)
                .HasMaxLength(5)
                .IsFixedLength()
                .HasColumnName("listen_language");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.Role)
                .HasMaxLength(20)
                .HasDefaultValueSql("'participant'::character varying")
                .HasColumnName("role");
            entity.Property(e => e.SpeakLanguage)
                .HasMaxLength(5)
                .IsFixedLength()
                .HasColumnName("speak_language");
            entity.Property(e => e.Status)
                .HasColumnName("status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomParticipants)
                .HasForeignKey(d => d.TranslationRoomId)
                .HasConstraintName("translation_room_participants_translation_room_id_fkey");
        });

        modelBuilder.HasPostgresEnum<RoomStatus>("translation_room", "room_status");
        modelBuilder.HasPostgresEnum<TranslationRoomParticipantStatus>("translation_room", "participant_status");
        modelBuilder.HasPostgresEnum<ArtifactType>("translation_room", "artifact_type");

        modelBuilder.Entity<TranslationRoomArtifact>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_artifacts_pkey");

            entity.ToTable("translation_room_artifacts", "translation_room");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("public.uuid_generate_v7()")
                .HasColumnName("id");

            entity.Property(e => e.TranslationRoomId)
                .HasColumnName("translation_room_id");

            entity.Property(e => e.ArtifactType)
                .HasColumnName("artifact_type");

            entity.Property(e => e.FileUrl)
                .HasMaxLength(500)
                .HasColumnName("file_url");

            entity.Property(e => e.FileFormat)
                .HasMaxLength(20)
                .HasColumnName("file_format");

            entity.Property(e => e.FileSizeBytes)
                .HasColumnName("file_size_bytes");

            entity.Property(e => e.ContainsRawAudio)
                .HasDefaultValue(false)
                .HasColumnName("contains_raw_audio");

            entity.Property(e => e.ContainsRawVideo)
                .HasDefaultValue(false)
                .HasColumnName("contains_raw_video");

            entity.Property(e => e.ConsentRequired)
                .HasDefaultValue(false)
                .HasColumnName("consent_required");

            entity.Property(e => e.RetentionUntil)
                .HasColumnName("retention_until");

            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");

            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");

            entity.Property(e => e.CreatedBy)
                .HasColumnName("created_by");

            entity.Property(e => e.DeletedAt)
                .HasColumnName("deleted_at");

            entity.Property(e => e.DeletedBy)
                .HasColumnName("deleted_by");

            entity.HasOne(d => d.TranslationRoom)
                .WithMany(p => p.TranslationRoomArtifacts)
                .HasForeignKey(d => d.TranslationRoomId)
                .HasConstraintName("translation_room_artifacts_translation_room_id_fkey");
        });
    }
}
