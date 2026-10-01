using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Infrastructure.Persistence;

// Safe from re-scaffold: scaffold only writes TranslationRoomDbContext.cs, never files under Partials/.
public partial class TranslationRoomDbContext
{
    /// <summary>Must stay textually identical to the migration's index predicate.</summary>
    internal const string BridgeRoomIndexFilter =
        "external_meeting_code IS NOT NULL AND translation_room_type = 'EXTERNAL_BRIDGE' " +
        "AND status NOT IN ('ENDED', 'CANCELLED', 'EXPIRED', 'FAILED') AND deleted_at IS NULL";

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TranslationRoomArtifact>(entity =>
        {
            entity.Property(e => e.ArtifactType).HasColumnName("artifact_type");
        });

        // `settings` and `target_languages` are jsonb columns.
        // Serialization is handled manually in TranslationRoomMapper / LanguageHelper.
        modelBuilder.Entity<TranslationRoom>(entity =>
        {
            entity.Property(e => e.Settings)
                .HasColumnType("jsonb");

            entity.Property(e => e.TargetLanguages)
                .HasColumnType("jsonb");

            // One shared bridge room per Google Meet code — migration
            // 20261001090000_bridge_one_room_per_meet_code.sql. Mapped here rather than in the
            // scaffold so a re-scaffold cannot drop it.
            entity.Property(e => e.ExternalMeetingCode)
                .HasMaxLength(32)
                .HasColumnName("external_meeting_code");
            entity.Property(e => e.BridgeCapturerUserId)
                .HasColumnName("bridge_capturer_user_id");
            entity.Property(e => e.BridgeCapturerHeartbeatAt)
                .HasColumnName("bridge_capturer_heartbeat_at");
            entity.Ignore(e => e.BridgeAudioOwnerId);
            entity.HasIndex(e => new { e.WorkspaceId, e.ExternalMeetingCode }, "translation_rooms_open_bridge_meet_code_key")
                .IsUnique()
                .HasFilter(BridgeRoomIndexFilter);
        });
    }
}
