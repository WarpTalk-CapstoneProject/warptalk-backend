using System;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Mappers;

/// <summary>
/// WT-655: RoomArtifactDto carries the recording's start instant too.
///
/// TranslationRoomArtifactDto has carried RecordingStartedAt since WT-473, which is why the
/// meeting-record page — the one consumer that happens to go through that DTO — can seek a
/// recording to a transcript position. Everything that reached artifacts through RoomArtifactDto
/// saw the field missing and therefore every recording as non-seekable, indistinguishable from a
/// recording that genuinely has no start instant.
///
/// Both directions are asserted on purpose. The value must travel; the absence must travel too, as
/// null. The failure mode this guards is not "the field is missing" but "the field is present and
/// filled in with something plausible" — a zero, or CreatedAt, which is stamped when egress
/// FINISHED and would seek every historical recording to a silently wrong position.
/// </summary>
public class RoomArtifactRecordingStartTests
{
    [Fact]
    public void ARecordingCarriesItsStartInstantThrough()
    {
        var startedAt = new DateTime(2026, 9, 8, 14, 30, 0, DateTimeKind.Utc);
        var artifact = Recording();
        artifact.RecordingStartedAt = startedAt;

        artifact.ToDto().RecordingStartedAt.Should().Be(startedAt);
    }

    [Fact]
    public void ARecordingWithNoKnownStartMapsToNull()
    {
        var artifact = Recording();
        artifact.RecordingStartedAt = null;

        var dto = artifact.ToDto();

        // Null means NOT KNOWN. Neither of the two tempting substitutes may appear here.
        dto.RecordingStartedAt.Should().BeNull();
        dto.RecordingStartedAt.Should().NotBe(default(DateTime));
        dto.RecordingStartedAt.Should().NotBe(dto.CreatedAt);
    }

    private static TranslationRoomArtifact Recording() => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = Guid.NewGuid(),
        ArtifactType = "OPTIONAL_RECORDING",
        FileFormat = "mp4",
        FileUrl = "https://storage.example/recording.mp4",
        FileSizeBytes = 4096,
        ContainsRawAudio = true,
        ContainsRawVideo = true,
        ConsentRequired = true,
        Status = "COMPLETED",
        // Egress finished well after the recording began — the gap is the whole reason CreatedAt
        // cannot stand in for RecordingStartedAt.
        CreatedAt = new DateTime(2026, 9, 8, 15, 12, 0, DateTimeKind.Utc)
    };
}
