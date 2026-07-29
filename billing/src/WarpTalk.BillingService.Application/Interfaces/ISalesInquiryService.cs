using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ISalesInquiryService
{
    Task<Result<SalesInquiryDto>> CreateAsync(CreateSalesInquiryRequest request, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> CreateWorkspaceAsync(CreateWorkspaceSalesInquiryRequest request, CancellationToken cancellationToken = default);
    Task<Result<PaginatedResponse<SalesInquiryDto>>> GetAsync(SalesInquiryQuery query, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> UpdateStatusAsync(Guid id, UpdateSalesInquiryStatusRequest request, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> LinkWorkspaceAsync(Guid id, LinkSalesInquiryWorkspaceRequest request, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> ConvertToContractAsync(Guid id, ConvertSalesInquiryToContractRequest request, CancellationToken cancellationToken = default);
}
