using System;

namespace WarpTalk.BillingService.Application.DTOs;

public record QuotaDeductRequest(
    Guid WorkspaceId,
    Guid SessionId,
    decimal ConsumedMinutes,
    string Source
);
