using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services.Interface; 

public interface IPaymentService
{
    Task<PaymentLinkResponse> CreatePaymentLinkAsync(
        Guid workspaceId,
        CreatePaymentLinkRequest request,
        CancellationToken cancellationToken = default);

    Task<PaymentLinkResponse> CreatePaymentLinkByOwnerAsync(
        Guid ownerUserId,
        CreatePaymentLinkRequest request,
        CancellationToken cancellationToken = default);

    Task<PayOsWebhookProcessResult> ProcessPayOsWebhookAsync(
        PayOsWebhookPayload payload,
        CancellationToken cancellationToken = default);
}
