using System;
using System.Collections.Generic;

namespace WarpTalk.PaymentService.Application.DTOs;

public record CheckoutSessionDto(
    string Id,
    long? AmountTotal,
    string Currency,
    IReadOnlyDictionary<string, string> Metadata,
    string PaymentStatus,
    string Status,
    string PaymentIntentId
);
