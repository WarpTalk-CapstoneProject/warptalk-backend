using System;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

public class TranslationRoomInvitation
{
    public Guid Id { get; set; }
    public Guid TranslationRoomId { get; set; }
    public string Email { get; set; } = null!;
    // Status can be: PENDING, ACCEPTED, DECLINED
    public string Status { get; set; } = "PENDING";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;
}
