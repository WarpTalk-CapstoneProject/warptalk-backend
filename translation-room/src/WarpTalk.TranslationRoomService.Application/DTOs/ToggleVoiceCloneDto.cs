using System.ComponentModel.DataAnnotations;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public class ToggleVoiceCloneDto
{
    [Required]
    public bool VoiceCloneEnabled { get; set; }
}
