using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services;

public interface IPaymentService
{
    Task ProcessPayOsWebhookAsync(PayOsWebhookPayload payload, CancellationToken cancellationToken = default);
    Task<IEnumerable<Transaction>> GetTransactionsByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IEnumerable<QuotaAuditLog>> GetUsageLogsByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<PaymentLinkResponse> CreatePaymentLinkAsync(Guid workspaceId, CreatePaymentLinkRequest request, CancellationToken cancellationToken = default);
}

