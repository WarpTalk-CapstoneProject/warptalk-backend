using System;
using WarpTalk.TranscriptService.Domain.Enums;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class TranscriptCorrection
{
    public CorrectionType CorrectionType { get; set; }
    public CorrectionStatus Status { get; set; }
}
