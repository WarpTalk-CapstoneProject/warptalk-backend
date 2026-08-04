using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ISalesInquiryService
{
    Task<Result<SalesInquiryDto>> CreatePublicInquiryAsync(CreateSalesInquiryRequest request, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> CreateWorkspaceInquiryAsync(CreateWorkspaceSalesInquiryRequest request, CancellationToken cancellationToken = default);
    Task<Result<PaginatedResponse<SalesInquiryDto>>> GetSalesInquiriesAsync(SalesInquiryQuery query, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> GetSalesInquiryByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> UpdateSalesInquiryStatusAsync(Guid id, UpdateSalesInquiryStatusRequest request, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> LinkSalesInquiryWorkspaceAsync(Guid id, LinkSalesInquiryWorkspaceRequest request, CancellationToken cancellationToken = default);
    Task<Result<SalesInquiryDto>> ConvertSalesInquiryToContractAsync(Guid id, ConvertSalesInquiryToContractRequest request, CancellationToken cancellationToken = default);
}
