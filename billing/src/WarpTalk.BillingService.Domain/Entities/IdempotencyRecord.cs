using System;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class IdempotencyRecord
{
    public Guid Id { get; set; }

    public string Key { get; set; } = null!;

    public string Operation { get; set; } = null!;

    public Guid? WorkspaceId { get; set; }

    public string RequestHash { get; set; } = null!;

    public string ResponseJson { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}