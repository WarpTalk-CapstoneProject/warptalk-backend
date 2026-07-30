using System;

namespace WarpTalk.BillingService.Application.DTOs;

public record ReserveCreditsRequest(
    Guid HostWorkspaceId,
    string IdempotencyKey,
    int AudioSeconds,
    int TokenCount,
    int GpuInferenceMs,
    bool IsVoiceClone
);

public record CreditReservationDto(
    Guid Id,
    Guid SubscriptionId,
    string IdempotencyKey,
    int Amount,
    string Status,
    DateTime ExpiresAt
);
