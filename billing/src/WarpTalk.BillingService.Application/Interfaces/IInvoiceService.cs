using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IInvoiceService
{
    Task<Result<PagedResult<InvoiceDto>>> GetInvoicesAsync(
        Guid workspaceId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<InvoiceDto>>> GetGlobalInvoicesAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
}
