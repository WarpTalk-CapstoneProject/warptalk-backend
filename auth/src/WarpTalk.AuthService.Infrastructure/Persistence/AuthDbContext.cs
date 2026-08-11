using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Infrastructure.Persistence;

public partial class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Permission> Permissions { get; set; }

    public virtual DbSet<RefreshToken> RefreshTokens { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<RolePermission> RolePermissions { get; set; }


    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserRole> UserRoles { get; set; }

    public virtual DbSet<UserSetting> UserSettings { get; set; }

    public virtual DbSet<VoiceConsent> VoiceConsents { get; set; }

    public virtual DbSet<VoiceProfile> VoiceProfiles { get; set; }

    public virtual DbSet<VoiceSample> VoiceSamples { get; set; }



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
            .HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<Permission>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("permissions_pkey");

            entity.ToTable("permissions", "auth");

            entity.HasIndex(e => e.Code, "permissions_code_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(100)
                .HasColumnName("code");
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
            entity.Property(e => e.Description)
                .HasMaxLength(255)
                .HasColumnName("description");
            entity.Property(e => e.GroupName)
                .HasMaxLength(50)
                .HasColumnName("group_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("updated_by");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.PermissionCreatedByNavigations)
                .HasForeignKey(d => d.CreatedBy)
                .HasConstraintName("permissions_created_by_fkey");

            entity.HasOne(d => d.DeletedByNavigation).WithMany(p => p.PermissionDeletedByNavigations)
                .HasForeignKey(d => d.DeletedBy)
                .HasConstraintName("permissions_deleted_by_fkey");

            entity.HasOne(d => d.UpdatedByNavigation).WithMany(p => p.PermissionUpdatedByNavigations)
                .HasForeignKey(d => d.UpdatedBy)
                .HasConstraintName("permissions_updated_by_fkey");
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("refresh_tokens_pkey");

            entity.ToTable("refresh_tokens", "auth");

            entity.HasIndex(e => e.TokenHash, "refresh_tokens_token_hash_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeviceInfo)
                .HasMaxLength(255)
                .HasColumnName("device_info");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.FamilyId)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("family_id");
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .HasColumnName("ip_address");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.TokenHash)
                .HasMaxLength(255)
                .HasColumnName("token_hash");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.User).WithMany(p => p.RefreshTokens)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("refresh_tokens_user_id_fkey");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("roles_pkey");

            entity.ToTable("roles", "auth");

            entity.HasIndex(e => e.Name, "roles_name_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
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
            entity.Property(e => e.Description)
                .HasMaxLength(255)
                .HasColumnName("description");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.IsSystem).HasColumnName("is_system");
            entity.Property(e => e.Name)
                .HasMaxLength(50)
                .HasColumnName("name");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("updated_by");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.RoleCreatedByNavigations)
                .HasForeignKey(d => d.CreatedBy)
                .HasConstraintName("roles_created_by_fkey");

            entity.HasOne(d => d.DeletedByNavigation).WithMany(p => p.RoleDeletedByNavigations)
                .HasForeignKey(d => d.DeletedBy)
                .HasConstraintName("roles_deleted_by_fkey");

            entity.HasOne(d => d.UpdatedByNavigation).WithMany(p => p.RoleUpdatedByNavigations)
                .HasForeignKey(d => d.UpdatedBy)
                .HasConstraintName("roles_updated_by_fkey");
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(e => new { e.RoleId, e.PermissionId }).HasName("role_permissions_pkey");

            entity.ToTable("role_permissions", "auth");

            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.PermissionId).HasColumnName("permission_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("created_by");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.RolePermissions)
                .HasForeignKey(d => d.CreatedBy)
                .HasConstraintName("role_permissions_created_by_fkey");

            entity.HasOne(d => d.Permission).WithMany(p => p.RolePermissions)
                .HasForeignKey(d => d.PermissionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("role_permissions_permission_id_fkey");

            entity.HasOne(d => d.Role).WithMany(p => p.RolePermissions)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("role_permissions_role_id_fkey");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("users_pkey");

            entity.ToTable("users", "auth");

            entity.HasIndex(e => e.Email, "users_email_key").IsUnique();

            entity.HasIndex(e => e.GoogleId, "users_google_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AvatarUrl)
                .HasMaxLength(500)
                .HasColumnName("avatar_url");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("Internal auth user reference. Nullable for system-created users.")
                .HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("deleted_by");
            entity.Property(e => e.Email)
                .HasMaxLength(320)
                .HasColumnName("email");
            entity.Property(e => e.EmailVerified).HasColumnName("email_verified");
            entity.Property(e => e.EmailVerifiedAt).HasColumnName("email_verified_at");
            entity.Property(e => e.EmailVerificationTokenHash)
                .HasMaxLength(64)
                .HasColumnName("email_verification_token_hash");
            entity.Property(e => e.EmailVerificationTokenExpiresAt)
                .HasColumnName("email_verification_token_expires_at");
            entity.Property(e => e.FailedLoginAttempts).HasColumnName("failed_login_attempts");
            entity.Property(e => e.FullName)
                .HasMaxLength(150)
                .HasColumnName("full_name");
            entity.Property(e => e.GoogleId)
                .HasMaxLength(255)
                .HasColumnName("google_id");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.IsLocked).HasColumnName("is_locked");
            entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(e => e.LastLoginIp)
                .HasMaxLength(45)
                .HasColumnName("last_login_ip");
            entity.Property(e => e.LockedUntil).HasColumnName("locked_until");
            entity.Property(e => e.PasswordHash)
                .HasMaxLength(255)
                .HasColumnName("password_hash");
            entity.Property(e => e.PasswordResetTokenHash)
                .HasMaxLength(64)
                .HasColumnName("password_reset_token_hash");
            entity.Property(e => e.PasswordResetTokenExpiresAt)
                .HasColumnName("password_reset_token_expires_at");
            entity.Property(e => e.Phone)
                .HasMaxLength(20)
                .HasColumnName("phone");
            entity.Property(e => e.PreferredLanguage)
                .HasMaxLength(15)
                .HasDefaultValueSql("'vi-VN'::character varying")
                .HasColumnName("preferred_language");
            entity.Property(e => e.Timezone)
                .HasMaxLength(50)
                .HasDefaultValueSql("'Asia/Ho_Chi_Minh'::character varying")
                .HasColumnName("timezone");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("updated_by");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.InverseCreatedByNavigation)
                .HasForeignKey(d => d.CreatedBy)
                .HasConstraintName("users_created_by_fkey");

            entity.HasOne(d => d.DeletedByNavigation).WithMany(p => p.InverseDeletedByNavigation)
                .HasForeignKey(d => d.DeletedBy)
                .HasConstraintName("users_deleted_by_fkey");

            entity.HasOne(d => d.UpdatedByNavigation).WithMany(p => p.InverseUpdatedByNavigation)
                .HasForeignKey(d => d.UpdatedBy)
                .HasConstraintName("users_updated_by_fkey");
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("user_roles_pkey");

            entity.ToTable("user_roles", "auth");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AssignedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("assigned_at");
            entity.Property(e => e.AssignedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("assigned_by");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.RevokedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("revoked_by");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.AssignedByNavigation).WithMany(p => p.UserRoleAssignedByNavigations)
                .HasForeignKey(d => d.AssignedBy)
                .HasConstraintName("user_roles_assigned_by_fkey");

            entity.HasOne(d => d.RevokedByNavigation).WithMany(p => p.UserRoleRevokedByNavigations)
                .HasForeignKey(d => d.RevokedBy)
                .HasConstraintName("user_roles_revoked_by_fkey");

            entity.HasOne(d => d.Role).WithMany(p => p.UserRoles)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("user_roles_role_id_fkey");

            entity.HasOne(d => d.User).WithMany(p => p.UserRoleUsers)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("user_roles_user_id_fkey");
        });

        modelBuilder.Entity<UserSetting>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("user_settings_pkey");

            entity.ToTable("user_settings", "auth");

            entity.HasIndex(e => e.UserId, "user_settings_user_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AutoGenerateSummary)
                .HasDefaultValue(true)
                .HasColumnName("auto_generate_summary");
            entity.Property(e => e.AutoRecordTranslationRooms).HasColumnName("auto_record_translation_rooms");
            entity.Property(e => e.DefaultListenLanguage)
                .HasMaxLength(15)
                .HasDefaultValueSql("'en-US'::character varying")
                .HasColumnName("default_listen_language");
            entity.Property(e => e.DefaultMaxParticipants)
                .HasDefaultValue(10)
                .HasColumnName("default_max_participants");
            entity.Property(e => e.DefaultSpeakLanguage)
                .HasMaxLength(15)
                .HasDefaultValueSql("'vi-VN'::character varying")
                .HasColumnName("default_speak_language");
            entity.Property(e => e.DefaultTranslationRoomType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'group'::character varying")
                .HasColumnName("default_translation_room_type");
            entity.Property(e => e.HighContrast).HasColumnName("high_contrast");
            entity.Property(e => e.MicNoiseSuppression)
                .HasDefaultValue(true)
                .HasColumnName("mic_noise_suppression");
            entity.Property(e => e.ScreenReaderMode).HasColumnName("screen_reader_mode");
            entity.Property(e => e.ShowOriginalTranscript)
                .HasDefaultValue(true)
                .HasColumnName("show_original_transcript");
            entity.Property(e => e.ShowTranslatedTranscript)
                .HasDefaultValue(true)
                .HasColumnName("show_translated_transcript");
            entity.Property(e => e.Theme)
                .HasMaxLength(10)
                .HasDefaultValueSql("'system'::character varying")
                .HasColumnName("theme");
            entity.Property(e => e.TranscriptFontSize)
                .HasDefaultValue(14)
                .HasColumnName("transcript_font_size");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("Internal auth user reference.")
                .HasColumnName("updated_by");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.VoiceCloneEnabled).HasColumnName("voice_clone_enabled");

            entity.HasOne(d => d.UpdatedByNavigation).WithMany(p => p.UserSettings)
                .HasForeignKey(d => d.UpdatedBy)
                .HasConstraintName("user_settings_updated_by_fkey");

            // Migration 018 fixed the real FK direction (user_settings.user_id ->
            // users.id — a settings row depends on a user, not the other way round),
            // but this scaffolded model was never regenerated to match: it previously
            // declared users.id as the FK referencing user_settings.user_id via a
            // "users_id_fkey" constraint that no longer exists in the database at all.
            // That backwards mapping made EF insert UserSetting before User in the same
            // SaveChanges batch, which the real constraint (user_settings_user_id_fkey)
            // then rejected. Configuring it here, on the actual dependent side, fixes
            // insert ordering to match reality.
            entity.HasOne(d => d.User).WithOne(p => p.IdNavigation)
                .HasForeignKey<UserSetting>(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("user_settings_user_id_fkey");
        });

        modelBuilder.Entity<VoiceConsent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("voice_consents_pkey");

            entity.ToTable("voice_consents", "voice");

            entity.HasIndex(e => new { e.UserId, e.ConsentType, e.CreatedAt }, "voice_consents_user_type_created_at_idx").IsDescending(false, false, true);

            entity.HasIndex(e => new { e.VoiceProfileId, e.ConsentStatus }, "voice_consents_voice_profile_id_status_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AiUseConfirmed).HasColumnName("ai_use_confirmed");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");
            entity.Property(e => e.VoiceProfileId).HasColumnName("voice_profile_id");
            entity.Property(e => e.ConsentType)
                .HasMaxLength(50)
                .HasColumnName("consent_type");
            entity.Property(e => e.ConsentStatus)
                .HasColumnType("consent_status")
                .HasColumnName("consent_status");
            entity.Property(e => e.ConsentTextVersion)
                .HasMaxLength(50)
                .HasColumnName("consent_text_version");
            entity.Property(e => e.ContractHash)
                .HasMaxLength(64)
                .HasColumnName("contract_hash");
            entity.Property(e => e.ContractSnapshot).HasColumnName("contract_snapshot");
            entity.Property(e => e.GrantedAt).HasColumnName("granted_at");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .HasColumnName("ip_address");
            entity.Property(e => e.NoImpersonationConfirmed).HasColumnName("no_impersonation_confirmed");
            entity.Property(e => e.OwnVoiceConfirmed).HasColumnName("own_voice_confirmed");
            entity.Property(e => e.RetentionAcknowledged).HasColumnName("retention_acknowledged");
            entity.Property(e => e.SyntheticVoiceAcknowledged).HasColumnName("synthetic_voice_acknowledged");
            entity.Property(e => e.UserAgent)
                .HasMaxLength(500)
                .HasColumnName("user_agent");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");

            entity.HasOne(d => d.VoiceProfile).WithMany(p => p.VoiceConsents)
                .HasForeignKey(d => d.VoiceProfileId)
                .HasConstraintName("voice_consents_voice_profile_id_fkey");
        });

        modelBuilder.Entity<VoiceProfile>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("voice_profiles_pkey");

            entity.ToTable("voice_profiles", "voice");

            entity.HasIndex(e => new { e.UserId, e.Status }, "voice_profiles_user_id_status_idx");

            entity.HasIndex(e => e.WorkspaceId, "voice_profiles_workspace_id_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");
            entity.Property(e => e.WorkspaceId)
                .HasComment("External AuthService workspace id. No physical FK.")
                .HasColumnName("workspace_id");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.Language)
                .HasMaxLength(15)
                .HasColumnName("language");
            entity.Property(e => e.Provider)
                .HasMaxLength(50)
                .HasColumnName("provider");
            entity.Property(e => e.EmbeddingRef)
                .HasMaxLength(500)
                .HasComment("Reference to voice embedding/model storage, not raw audio.")
                .HasColumnName("embedding_ref");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
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
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("deleted_by");
        });

        modelBuilder.Entity<VoiceSample>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("voice_samples_pkey");

            entity.ToTable("voice_samples", "voice");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.VoiceProfileId).HasColumnName("voice_profile_id");
            entity.Property(e => e.SampleType)
                .HasMaxLength(30)
                .HasColumnName("sample_type");
            entity.Property(e => e.FileUrl)
                .HasMaxLength(500)
                .HasColumnName("file_url");
            entity.Property(e => e.DurationSeconds).HasColumnName("duration_seconds");
            entity.Property(e => e.Language)
                .HasMaxLength(15)
                .HasColumnName("language");
            entity.Property(e => e.ContainsRawAudio)
                .HasDefaultValue(true)
                .HasColumnName("contains_raw_audio");
            entity.Property(e => e.RetentionUntil).HasColumnName("retention_until");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("deleted_by");

            entity.HasOne(d => d.VoiceProfile).WithMany(p => p.VoiceSamples)
                .HasForeignKey(d => d.VoiceProfileId)
                .HasConstraintName("voice_samples_voice_profile_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
