using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;

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

    public virtual DbSet<SchemaMigration> SchemaMigrations { get; set; }

    public virtual DbSet<SupportedLanguage> SupportedLanguages { get; set; }

    public virtual DbSet<TranslationRoom> TranslationRooms { get; set; }

    public virtual DbSet<TranslationRoomArtifact> TranslationRoomArtifacts { get; set; }

    public virtual DbSet<TranslationRoomAudioRoute> TranslationRoomAudioRoutes { get; set; }

    public virtual DbSet<TranslationRoomSession> TranslationRoomSessions { get; set; }

    public virtual DbSet<TranslationRoomFeedback> TranslationRoomFeedbacks { get; set; }

    public virtual DbSet<TranslationRoomParticipant> TranslationRoomParticipants { get; set; }

    public virtual DbSet<TranslationRoomInvitation> TranslationRoomInvitations { get; set; }

    /// <summary>WT-327: recurring bookings. Each one materialises into ordinary TranslationRooms.</summary>
    public virtual DbSet<TranslationRoomSeries> TranslationRoomSeries { get; set; }




    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresEnum("artifact_type", new[] { "TRANSCRIPT_EXPORT", "SUMMARY_EXPORT", "DEBUG_LOG", "OPTIONAL_RECORDING", "AUDIO_SAMPLE" })
            .HasPostgresEnum("consent_status", new[] { "GRANTED", "REVOKED", "EXPIRED" })
            .HasPostgresEnum("job_status", new[] { "QUEUED", "PROCESSING", "COMPLETED", "FAILED", "CANCELLED" })
            .HasPostgresEnum("notification_status", new[] { "PENDING", "SENT", "DELIVERED", "FAILED", "READ" })
            // WT-263: participant_status was dropped from the database by migration
            // 014-15-06-2026-convert-translation-and-transcript-enums-to-varchar.sql, which converted
            // translation_room_participants.status to VARCHAR(255). Nothing maps to the type any more
            // (the entity property is string, and Program.cs builds the data source without MapEnum),
            // so declaring it here only re-created a type the schema no longer has.
            .HasPostgresEnum("room_status", new[] { "SCHEDULED", "WAITING", "IN_PROGRESS", "PAUSED", "ENDED", "CANCELLED", "EXPIRED", "FAILED" })
            .HasPostgresEnum("ticket_status", new[] { "OPEN", "IN_PROGRESS", "RESOLVED", "CLOSED" })
            .HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<SchemaMigration>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("schema_migrations_pkey");

            entity.ToTable("schema_migrations", "translation_room");

            entity.HasIndex(e => e.MigrationKey, "schema_migrations_migration_key_key").IsUnique();

            entity.HasIndex(e => e.Status, "schema_migrations_status_idx");

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

        modelBuilder.Entity<SupportedLanguage>(entity =>
        {
            entity
                .HasKey(language => language.Code)
                .HasName("supported_languages_pkey");

            entity.ToTable("supported_languages", "translation_room");

            entity.Property(e => e.Code)
                .HasMaxLength(15)
                .HasColumnName("code");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Name)
                .HasMaxLength(100)
                .HasColumnName("name");
            entity.Property(e => e.NativeName)
                .HasMaxLength(100)
                .HasColumnName("native_name");
        });

        modelBuilder.Entity<TranslationRoom>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_rooms_pkey");

            entity.ToTable("translation_rooms", "translation_room", tb => tb.HasComment("Room lifecycle:\nSCHEDULED -> WAITING\nSCHEDULED -> CANCELLED\nSCHEDULED -> EXPIRED\nWAITING -> IN_PROGRESS\nWAITING -> CANCELLED\nWAITING -> EXPIRED\nIN_PROGRESS -> PAUSED\nPAUSED -> IN_PROGRESS\nIN_PROGRESS -> ENDED\nIN_PROGRESS -> FAILED\n\nDraft room is not persisted. If the user discards a draft, no room record is created.\n"));

            entity.HasIndex(e => new { e.HostId, e.CreatedAt }, "translation_rooms_host_id_created_at_idx");

            entity.HasIndex(e => new { e.Status, e.ScheduledAt }, "translation_rooms_status_scheduled_at_idx");

            // WT-327: unique for ONE-OFF rooms only. Every occurrence of a recurring booking
            // shares one code on purpose — one meeting, one link — so a table-wide unique index
            // would reject the second occurrence. The filter keeps the collision backstop exactly
            // where collisions can happen: RoomCodeGenerator mints at random for one-off rooms.
            // Occurrences are bounded instead by translation_rooms_series_id_occurrence_date_key.
            // Mirrored by migration 20260812090000_share_one_room_code_per_series.sql.
            entity.HasIndex(e => e.TranslationRoomCode, "translation_rooms_one_off_code_key")
                .IsUnique()
                .HasFilter("series_id IS NULL");

            entity.HasIndex(e => new { e.WorkspaceId, e.CreatedAt }, "translation_rooms_workspace_id_created_at_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("deleted_by");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.DurationSeconds).HasColumnName("duration_seconds");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
            entity.Property(e => e.HostId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("host_id");
            // WT-359: the handover, kept off host_id so a Transfer Host during one meeting does
            // not also move the booking, the series and the usage attribution.
            entity.Property(e => e.ActiveHostId)
                .HasComment("WT-359: who is running this meeting now, after any Transfer Host. NULL means the booker (host_id) still is. Effective host = COALESCE(active_host_id, host_id).")
                .HasColumnName("active_host_id");
            entity.HasIndex(e => e.ActiveHostId, "translation_rooms_active_host_id_idx")
                .HasFilter("active_host_id IS NOT NULL");
            // Computed from the two columns above; they are the stored state, this is the answer.
            entity.Ignore(e => e.EffectiveHostId);
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.MaxParticipants)
                .HasDefaultValue(10)
                .HasColumnName("max_participants");
            entity.Property(e => e.ScheduledAt).HasColumnName("scheduled_at");
            // WT-327
            entity.Property(e => e.SeriesId).HasColumnName("series_id");
            entity.Property(e => e.SeriesOccurrenceLocalDate)
                .HasColumnType("date")
                .HasColumnName("series_occurrence_local_date");
            entity.Property(e => e.Reminder30MinSentAt).HasColumnName("reminder_30min_sent_at");
            entity.Property(e => e.Reminder10MinSentAt).HasColumnName("reminder_10min_sent_at");
            entity.Property(e => e.Reminder1MinSentAt).HasColumnName("reminder_1min_sent_at");
            entity.Property(e => e.Settings)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("settings");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(15)
                .HasColumnName("source_language");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasMaxLength(255)
                .HasDefaultValueSql("'SCHEDULED'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TargetLanguages)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("target_languages");
            entity.Property(e => e.Title)
                .HasMaxLength(255)
                .HasColumnName("title");
            entity.Property(e => e.TranslationRoomCode)
                .HasMaxLength(12)
                .HasColumnName("translation_room_code");
            entity.Property(e => e.TranslationRoomType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'GROUP'::character varying")
                .HasColumnName("translation_room_type");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("updated_by");
            entity.Property(e => e.WorkspaceId)
                .HasComment("External AuthService workspace id. No physical FK.")
                .HasColumnName("workspace_id");

            // WT-327: the back-reference to the recurring booking this room came from. A REAL
            // foreign key, unlike the workspace/user ids above — the series lives in this same
            // logical database and schema, so there is no cross-service boundary to respect
            // (WT-263 dropped those). RESTRICT, not CASCADE: deleting a series must never take
            // the meetings it produced — and their transcripts, artifacts and billing — with it.
            entity.HasOne(e => e.Series)
                .WithMany(s => s.Occurrences)
                .HasForeignKey(e => e.SeriesId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("translation_rooms_series_id_fkey");

            // WT-327: THE idempotency guarantee of the materialisation sweep. One series can
            // hold at most one room per local date, so a double-run, a restart mid-pass, or two
            // service replicas sweeping at once cannot produce two rooms for the same day.
            entity.HasIndex(e => new { e.SeriesId, e.SeriesOccurrenceLocalDate },
                    "translation_rooms_series_id_occurrence_date_key")
                .IsUnique()
                .HasFilter("series_id IS NOT NULL");
        });

        modelBuilder.Entity<TranslationRoomSeries>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_series_pkey");

            entity.ToTable("translation_room_series", "translation_room", tb => tb.HasComment(
                "WT-327: a recurring booking rather than a meeting - each occurrence is materialised as an ordinary translation_rooms row linked by series_id"));

            // The sweep's own query: ACTIVE series whose horizon has not reached their end date.
            entity.HasIndex(e => new { e.Status, e.MaterializedThroughLocalDate },
                "translation_room_series_status_materialized_through_idx");

            entity.HasIndex(e => new { e.WorkspaceId, e.CreatedAt },
                "translation_room_series_workspace_id_created_at_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.WorkspaceId)
                .HasComment("External AuthService workspace id. No physical FK.")
                .HasColumnName("workspace_id");
            entity.Property(e => e.HostId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("host_id");
            entity.Property(e => e.RecurrenceType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'DAILY'::character varying")
                .HasColumnName("recurrence_type");
            entity.Property(e => e.RecurrenceInterval)
                .HasDefaultValue(1)
                .HasColumnName("recurrence_interval");
            entity.Property(e => e.RecurrenceByWeekdays)
                .HasColumnType("jsonb")
                .HasComment("WEEKLY only: ISO weekday numbers, e.g. [1,3,5]. Null for DAILY.")
                .HasColumnName("recurrence_by_weekdays");
            entity.Property(e => e.RecurrenceByMonthDay)
                .HasComment("MONTHLY only: day of month 1-31. Null for DAILY.")
                .HasColumnName("recurrence_by_month_day");
            entity.Property(e => e.StartTimeLocal)
                .HasColumnType("time without time zone")
                .HasColumnName("start_time_local");
            entity.Property(e => e.TimeZone)
                .HasMaxLength(64)
                .HasComment("IANA zone id, e.g. Asia/Ho_Chi_Minh. Never a UTC offset.")
                .HasColumnName("time_zone");
            entity.Property(e => e.StartsOnLocalDate)
                .HasColumnType("date")
                .HasColumnName("starts_on_local_date");
            entity.Property(e => e.EndsOnLocalDate)
                .HasColumnType("date")
                .HasComment("Inclusive. NOT NULL: a series must terminate.")
                .HasColumnName("ends_on_local_date");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'ACTIVE'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.MaterializedThroughLocalDate)
                .HasColumnType("date")
                .HasComment("Rolling-horizon watermark. Generation resumes strictly after this date.")
                .HasColumnName("materialized_through_local_date");
            entity.Property(e => e.Title)
                .HasMaxLength(255)
                .HasColumnName("title");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.TranslationRoomType)
                .HasMaxLength(20)
                .HasColumnName("translation_room_type");
            entity.Property(e => e.MaxParticipants)
                .HasDefaultValue(0)
                .HasComment("0 means 'let the meeting type decide', preserved rather than frozen at creation.")
                .HasColumnName("max_participants");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(15)
                .HasColumnName("source_language");
            entity.Property(e => e.TargetLanguages)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("target_languages");
            entity.Property(e => e.Settings)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("settings");
            entity.Property(e => e.InvitedEmails)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("invited_emails");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("updated_by");
        });

        modelBuilder.Entity<TranslationRoomArtifact>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_artifacts_pkey");

            entity.ToTable("translation_room_artifacts", "translation_room");

            entity.HasIndex(e => e.RetentionUntil, "translation_room_artifacts_retention_until_idx");

            entity.HasIndex(e => new { e.TranslationRoomId, e.ArtifactType }, "translation_room_artifacts_translation_room_id_artifact_typ_idx");

            entity.HasIndex(e => e.ProviderArtifactId, "translation_room_artifacts_provider_artifact_id_key")
                .IsUnique()
                .HasFilter("provider_artifact_id IS NOT NULL");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.ArtifactType)
                .HasMaxLength(255)
                .HasColumnName("artifact_type");
            entity.Property(e => e.ConsentRequired).HasColumnName("consent_required");
            entity.Property(e => e.ContainsRawAudio).HasColumnName("contains_raw_audio");
            entity.Property(e => e.ContainsRawVideo).HasColumnName("contains_raw_video");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            // WT-473. Explicit, like every other column here: this context hand-maps each one, and
            // a missing HasColumnName 500s every SELECT over the table rather than failing loudly
            // at startup.
            entity.Property(e => e.RecordingStartedAt).HasColumnName("recording_started_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("deleted_by");
            entity.Property(e => e.Content).HasColumnName("content");
            entity.Property(e => e.FileFormat)
                .HasMaxLength(20)
                .HasColumnName("file_format");
            entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes");
            // WT-473. Explicit, like every other column here: this context hand-maps each one, and
            // a missing HasColumnName 500s every SELECT over the table rather than failing loudly
            // at startup.
            entity.Property(e => e.RecordingStartedAt).HasColumnName("recording_started_at");
            entity.Property(e => e.FileUrl)
                .HasMaxLength(500)
                .HasColumnName("file_url");
            entity.Property(e => e.ProviderArtifactId)
                .HasMaxLength(255)
                .HasColumnName("provider_artifact_id");
            entity.Property(e => e.RetentionUntil).HasColumnName("retention_until");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomArtifacts)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_artifacts_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomAudioRoute>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_audio_routes_pkey");

            entity.ToTable("translation_room_audio_routes", "translation_room");

            entity.HasIndex(e => new { e.SourceParticipantId, e.TargetParticipantId }, "translation_room_audio_routes_source_participant_id_target__idx");

            entity.HasIndex(e => new { e.TranslationRoomId, e.Status }, "translation_room_audio_routes_translation_room_id_status_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(15)
                .HasColumnName("source_language");
            entity.Property(e => e.SourceParticipantId).HasColumnName("source_participant_id");
            entity.Property(e => e.StartedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.StreamId)
                .HasMaxLength(100)
                .HasColumnName("stream_id");
            entity.Property(e => e.TargetLanguage)
                .HasMaxLength(15)
                .HasColumnName("target_language");
            entity.Property(e => e.TargetParticipantId).HasColumnName("target_participant_id");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.VoiceCloneEnabled).HasColumnName("voice_clone_enabled");

            entity.HasOne(d => d.SourceParticipant).WithMany(p => p.TranslationRoomAudioRouteSourceParticipants)
                .HasForeignKey(d => d.SourceParticipantId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_audio_routes_source_participant_id_fkey");

            entity.HasOne(d => d.TargetParticipant).WithMany(p => p.TranslationRoomAudioRouteTargetParticipants)
                .HasForeignKey(d => d.TargetParticipantId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_audio_routes_target_participant_id_fkey");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomAudioRoutes)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_audio_routes_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomSession>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_sessions_pkey");

            entity.ToTable("translation_room_sessions", "translation_room");

            entity.HasIndex(e => new { e.TranslationRoomId, e.Status }, "translation_room_sessions_translation_room_id_status_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.MainLanguage)
                .HasMaxLength(15)
                .HasColumnName("main_language");
            entity.Property(e => e.AudioUrl)
                .HasMaxLength(500)
                .HasColumnName("audio_url");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'ACTIVE'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomSessions)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_sessions_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomFeedback>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_feedback_pkey");

            entity.ToTable("translation_room_feedback", "translation_room");

            entity.HasIndex(e => new { e.TranslationRoomId, e.UserId }, "translation_room_feedback_translation_room_id_user_id_idx").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AiSummaryQuality).HasColumnName("ai_summary_quality");
            entity.Property(e => e.AudioQuality).HasColumnName("audio_quality");
            entity.Property(e => e.Comments).HasColumnName("comments");
            entity.Property(e => e.CommunicationInsights)
                .HasColumnType("jsonb")
                .HasColumnName("communication_insights");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.OverallRating).HasColumnName("overall_rating");
            entity.Property(e => e.TranslationQuality).HasColumnName("translation_quality");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");
            entity.Property(e => e.VoiceCloneQuality).HasColumnName("voice_clone_quality");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomFeedbacks)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_feedback_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomParticipant>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_participants_pkey");

            entity.ToTable("translation_room_participants", "translation_room", tb => tb.HasComment("Participant lifecycle:\nINVITED -> WAITING\nWAITING -> CONNECTED\nWAITING -> REJECTED\nCONNECTED -> DISCONNECTED\nDISCONNECTED -> CONNECTED\nCONNECTED -> LEFT\nCONNECTED -> KICKED\n\nMUTED is not a participant_status. It is represented by is_muted.\n"));

            entity.HasIndex(e => new { e.TranslationRoomId, e.Status }, "translation_room_participants_translation_room_id_status_idx");

            entity.HasIndex(e => new { e.TranslationRoomId, e.UserId }, "translation_room_participants_translation_room_id_user_id_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.ConnectionType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'webrtc'::character varying")
                .HasColumnName("connection_type");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.IsTranslationAudioEnabled)
                .HasDefaultValue(true)
                .HasColumnName("is_translation_audio_enabled");
            entity.Property(e => e.IsUsingVoiceClone).HasColumnName("is_using_voice_clone");
            // WT-446. Every column in this context is hand-mapped — there is no snake_case naming
            // convention to fall back on — so a missing HasColumnName here would not be a cosmetic
            // gap: EF would look for "IsExternal" and every SELECT over the roster would 500.
            entity.Property(e => e.IsExternal)
                .HasDefaultValue(false)
                .HasColumnName("is_external");
            entity.Property(e => e.JoinedAt).HasColumnName("joined_at");
            entity.Property(e => e.LeftAt).HasColumnName("left_at");
            entity.Property(e => e.ListenLanguage)
                .HasMaxLength(15)
                .HasColumnName("listen_language");
            entity.Property(e => e.Role)
                .HasMaxLength(20)
                .HasDefaultValueSql("'participant'::character varying")
                .HasColumnName("role");
            entity.Property(e => e.SpeakLanguage)
                .HasMaxLength(15)
                .HasColumnName("speak_language");
            entity.Property(e => e.Status)
                .HasMaxLength(255)
                .HasDefaultValueSql("'INVITED'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. Nullable for guests. No physical FK.")
                .HasColumnName("user_id");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomParticipants)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_participants_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomInvitation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_invitations_pkey");

            entity.ToTable("translation_room_invitations", "translation_room");

            entity.HasIndex(e => new { e.TranslationRoomId, e.Email }, "translation_room_invitations_room_email_idx").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.Email)
                .HasMaxLength(255)
                .HasColumnName("email");
            entity.Property(e => e.Status)
                .HasMaxLength(50)
                .HasDefaultValueSql("'PENDING'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomInvitations)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("translation_room_invitations_translation_room_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
