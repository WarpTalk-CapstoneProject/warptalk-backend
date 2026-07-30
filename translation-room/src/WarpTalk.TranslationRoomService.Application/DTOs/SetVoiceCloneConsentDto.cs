using System.ComponentModel.DataAnnotations;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public class SetVoiceCloneConsentDto
{
    [Required]
    public bool Enabled { get; set; }
}
