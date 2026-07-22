using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IInvoiceService
{
    Task<Result<PaginatedResponse<InvoiceDto>>> GetInvoicesAsync(Guid workspaceId, PaginationQuery query, CancellationToken cancellationToken = default);
    Task<Result<PaginatedResponse<InvoiceDto>>> GetGlobalInvoicesAsync(PaginationQuery query, CancellationToken cancellationToken = default);
}
