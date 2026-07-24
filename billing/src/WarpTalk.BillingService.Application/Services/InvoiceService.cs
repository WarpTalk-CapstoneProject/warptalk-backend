using System;
using WarpTalk.BillingService.Domain.Constants;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class InvoiceService : IInvoiceService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<InvoiceService> _logger;
    private readonly IWorkspaceClient _workspaceClient;

    public InvoiceService(
        IUnitOfWork unitOfWork,
        ILogger<InvoiceService> logger,
        IWorkspaceClient workspaceClient)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _workspaceClient = workspaceClient;
    }

    public async Task<Result<PaginatedResponse<InvoiceDto>>> GetInvoicesAsync(
        Guid workspaceId, PaginationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _unitOfWork.InvoiceRepository.GetPageAsync(
                BillingQueryHelper.ToPageRequest(query),
                workspaceId,
                cancellationToken);

            var dtos = page.Items.Select(i => i.ToDto(workspaceId)).ToList();
            return Result.Success(PaginatedResponse<InvoiceDto>.Create(dtos, page.TotalCount, page.PageNumber, page.PageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingInvoices, workspaceId);
            return Result.Failure<PaginatedResponse<InvoiceDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PaginatedResponse<InvoiceDto>>> GetGlobalInvoicesAsync(
        PaginationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _unitOfWork.InvoiceRepository.GetPageAsync(
                BillingQueryHelper.ToPageRequest(query),
                null,
                cancellationToken);

            var dtos = page.Items.Select(i => i.ToDto(i.Payment.Subscription.WorkspaceId)).ToList();

            // Resolve workspace names cross-schema
            try
            {
                var workspaceIds = BillingQueryHelper.GetWorkspaceIds(page.Items);
                if (workspaceIds.Length > 0)
                {
                    var workspaceNames = await _unitOfWork.CreditTransactionRepository.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);
                    dtos = BillingQueryHelper.ApplyWorkspaceNames(dtos, workspaceNames);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNamesGlobalInvoices);
            }

            return Result.Success(PaginatedResponse<InvoiceDto>.Create(dtos, page.TotalCount, page.PageNumber, page.PageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingGlobalInvoices);
            return Result.Failure<PaginatedResponse<InvoiceDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }
}
