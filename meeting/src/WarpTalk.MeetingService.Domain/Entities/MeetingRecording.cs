using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Infrastructure;

public partial class MeetingRecording
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }

    public string RecordingType { get; set; } = null!;

    public string FileUrl { get; set; } = null!;

    public string FileFormat { get; set; } = null!;

    public long FileSizeBytes { get; set; }

    public int DurationSeconds { get; set; }

    public string? Language { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual Meeting Meeting { get; set; } = null!;
}
