using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

public partial class TranslationRoomRecording
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    public string RecordingType { get; set; } = null!;

    public string FileUrl { get; set; } = null!;

    public string FileFormat { get; set; } = null!;

    public long FileSizeBytes { get; set; }

    public int DurationSeconds { get; set; }

    public string? Language { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;
}

