using System;

namespace WarpTalk.NotificationService.Domain.Entities
{
    public class NotificationMessage
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string Type { get; set; } = null!;
        public string Title { get; set; } = null!;
        public string Content { get; set; } = null!;
        public string? ActionUrl { get; set; }
        public string PayloadJson { get; set; } = null!;
        public bool IsRead { get; set; }
        public DateTime? ReadAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
