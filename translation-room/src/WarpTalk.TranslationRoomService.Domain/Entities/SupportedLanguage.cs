using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

public partial class SupportedLanguage
{
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? NativeName { get; set; }

    public bool IsActive { get; set; }
}
