using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Domain.Entities;

public partial class MeetingChatTranslation
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public Guid MeetingRoomId { get; set; }

    public string SourceLanguage { get; set; } = null!;

    public string TargetLanguage { get; set; } = null!;

    public string TranslatedText { get; set; } = null!;

    public string? ModelUsed { get; set; }

    public int PromptVersion { get; set; }

    public decimal? Confidence { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual MeetingChatMessage Message { get; set; } = null!;
}
