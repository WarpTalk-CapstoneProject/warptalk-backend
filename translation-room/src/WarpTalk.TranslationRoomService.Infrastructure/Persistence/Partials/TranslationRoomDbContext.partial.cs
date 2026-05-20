using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Infrastructure.Persistence;

public partial class TranslationRoomDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<RoomStatus>("translation_room", "room_status");
        modelBuilder.HasPostgresEnum<TranslationRoomParticipantStatus>("translation_room", "participant_status");
        modelBuilder.HasPostgresEnum<ArtifactType>("translation_room", "artifact_type");

        modelBuilder.Entity<TranslationRoomArtifact>(entity =>
        {
            entity.Property(e => e.ArtifactType).HasColumnName("artifact_type");
        });
    }
}
