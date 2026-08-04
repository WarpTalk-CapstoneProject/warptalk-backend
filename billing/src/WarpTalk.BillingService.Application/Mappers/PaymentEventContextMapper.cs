using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Models;

namespace WarpTalk.BillingService.Application.Mappers;

public static class PaymentEventContextMapper
{
    public static PaymentEventContext ToPaymentEventContext(
        this StripePaymentEventRequest request,
        Guid workspaceId,
        Guid userId,
        string providerTransactionId,
        string parsedPaymentStatus,
        Payment? existingPayment,
        Subscription? subscription)
        => new(
            request,
            workspaceId,
            userId,
            providerTransactionId,
            parsedPaymentStatus,
            existingPayment?.Id ?? Guid.NewGuid(),
            existingPayment,
            subscription);
}
