using Microsoft.EntityFrameworkCore;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Enums;

namespace WarpTalk.MeetingService.Infrastructure.Data;

public class MeetingDbContext : DbContext
{
    public MeetingDbContext(DbContextOptions<MeetingDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<MeetingRoom> MeetingRooms { get; set; } = null!;
    public virtual DbSet<MeetingParticipant> MeetingParticipants { get; set; } = null!;
    public virtual DbSet<MeetingTrack> MeetingTracks { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("uuid-ossp");
        
        modelBuilder.HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<MeetingRoom>(entity =>
        {
            entity.ToTable("meeting_rooms", "meeting");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.TranslationRoomId, "idx_meeting_rooms_translation_room_id");

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuid_generate_v7()");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.ProviderRoomName).HasColumnName("provider_room_name").HasMaxLength(255);
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasDefaultValue(MeetingStatus.Created);
            
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
        });

        modelBuilder.Entity<MeetingParticipant>(entity =>
        {
            entity.ToTable("meeting_participants", "meeting");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.MeetingRoomId, "idx_meeting_participants_meeting_room_id");
            entity.HasIndex(e => e.UserId, "idx_meeting_participants_user_id");

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuid_generate_v7()");
            entity.Property(e => e.MeetingRoomId).HasColumnName("meeting_room_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.ProviderIdentity).HasColumnName("provider_identity").HasMaxLength(255);
            
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            
            entity.Property(e => e.JoinedAt).HasColumnName("joined_at");
            entity.Property(e => e.LeftAt).HasColumnName("left_at");

            entity.HasOne(d => d.MeetingRoom).WithMany(p => p.MeetingParticipants)
                .HasForeignKey(d => d.MeetingRoomId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("meeting_participants_meeting_room_id_fkey");
        });

        modelBuilder.Entity<MeetingTrack>(entity =>
        {
            entity.ToTable("meeting_tracks", "meeting");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.MeetingParticipantId, "idx_meeting_tracks_meeting_participant_id");

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuid_generate_v7()");
            entity.Property(e => e.MeetingParticipantId).HasColumnName("meeting_participant_id");
            entity.Property(e => e.ProviderTrackId).HasColumnName("provider_track_id").HasMaxLength(255);
            entity.Property(e => e.MediaType).HasColumnName("media_type").HasConversion<string>();
            entity.Property(e => e.IsMuted).HasColumnName("is_muted").HasDefaultValue(false);
            
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.UnpublishedAt).HasColumnName("unpublished_at");

            entity.HasOne(d => d.MeetingParticipant).WithMany(p => p.MeetingTracks)
                .HasForeignKey(d => d.MeetingParticipantId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("meeting_tracks_meeting_participant_id_fkey");
        });
    }
}
