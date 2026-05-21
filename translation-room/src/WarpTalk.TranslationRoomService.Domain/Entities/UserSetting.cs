using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

public partial class UserSetting
{
    public Guid? UserId { get; set; }

    public string? DefaultSpeakLanguage { get; set; }

    public string? DefaultListenLanguage { get; set; }
}
