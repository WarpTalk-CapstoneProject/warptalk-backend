using System;
using WarpTalk.TranscriptService.Domain.Enums;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class Transcript
{
    public TranscriptStatus Status { get; set; }
}
