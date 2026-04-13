using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Infrastructure;

public partial class TranslationRoomSummary
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    public string Summary { get; set; } = null!;

    public string KeyPoints { get; set; } = null!;

    public string Decisions { get; set; } = null!;

    public string ActionItems { get; set; } = null!;

    public string ModelUsed { get; set; } = null!;

    public int ProcessingTimeMs { get; set; }

    public DateTime GeneratedAt { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;
}
