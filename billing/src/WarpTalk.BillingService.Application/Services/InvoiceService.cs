using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class InvoiceService : IInvoiceService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(IUnitOfWork unitOfWork, ILogger<InvoiceService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<PagedResult<InvoiceDto>>> GetInvoicesAsync(
        Guid workspaceId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            var size = pageSize > 0 ? pageSize : 20;
            var skip = ((pageNumber > 0 ? pageNumber : 1) - 1) * size;

            var items = await _unitOfWork.InvoiceRepository.GetPagedAsync(
                i => i.Payment.Subscription.WorkspaceId == workspaceId,
                skip, size,
                q => q.OrderByDescending(i => i.CreatedAt),
                includes: new System.Linq.Expressions.Expression<Func<Invoice, object>>[] { i => i.Payment, i => i.Payment.Subscription },
                cancellationToken: cancellationToken);

            var total = await _unitOfWork.InvoiceRepository.CountAsync(
                i => i.Payment.Subscription.WorkspaceId == workspaceId,
                cancellationToken);

            var dtos = items.Select(i => new InvoiceDto
            {
                Id = i.Id.ToString(),
                StripeInvoiceId = i.InvoiceNumber,
                Amount = i.Total,
                Currency = i.Currency,
                Status = i.Status,
                InvoicePdfUrl = i.PdfUrl,
                HostedInvoiceUrl = string.Empty,
                InvoiceNumber = i.InvoiceNumber,
                Subtotal = i.Subtotal,
                Tax = i.Tax,
                Total = i.Total,
                PdfUrl = i.PdfUrl,
                LineItems = i.LineItems,
                IssuedAt = i.IssuedAt,
                DueAt = i.DueAt,
                PaidAt = i.PaidAt,
                CreatedAt = i.CreatedAt,
                WorkspaceId = workspaceId.ToString()
            });

            return Result.Success(new PagedResult<InvoiceDto>(total, dtos));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting invoices for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<InvoiceDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PagedResult<InvoiceDto>>> GetGlobalInvoicesAsync(
        int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            var size = pageSize > 0 ? pageSize : 20;
            var skip = ((pageNumber > 0 ? pageNumber : 1) - 1) * size;

            var items = await _unitOfWork.InvoiceRepository.GetPagedAsync(
                i => true, // All invoices
                skip, size,
                q => q.OrderByDescending(i => i.CreatedAt),
                includes: new System.Linq.Expressions.Expression<Func<Invoice, object>>[] { i => i.Payment },
                cancellationToken: cancellationToken);

            var total = await _unitOfWork.InvoiceRepository.CountAsync(
                i => true,
                cancellationToken);

            var dtos = items.Select(i => new InvoiceDto
            {
                Id = i.Id.ToString(),
                StripeInvoiceId = i.InvoiceNumber,
                Amount = i.Total,
                Currency = i.Currency,
                Status = i.Status,
                InvoicePdfUrl = i.PdfUrl,
                HostedInvoiceUrl = string.Empty,
                InvoiceNumber = i.InvoiceNumber,
                Subtotal = i.Subtotal,
                Tax = i.Tax,
                Total = i.Total,
                PdfUrl = i.PdfUrl,
                LineItems = i.LineItems,
                IssuedAt = i.IssuedAt,
                DueAt = i.DueAt,
                PaidAt = i.PaidAt,
                CreatedAt = i.CreatedAt,
                WorkspaceId = i.Payment.Subscription.WorkspaceId.ToString(),
                WorkspaceName = "Unknown Workspace"
            }).ToList();

            // Resolve workspace names
            try
            {
                var workspaceIds = items.Select(i => i.Payment.Subscription.WorkspaceId).Distinct().ToArray();
                if (workspaceIds.Length > 0)
                {
                    var connection = (Npgsql.NpgsqlConnection)_unitOfWork.GetDbConnection();
                    if (connection.State != System.Data.ConnectionState.Open)
                        await connection.OpenAsync(cancellationToken);

                    using var command = new Npgsql.NpgsqlCommand(
                        "SELECT id, name FROM workspace.workspaces WHERE id = ANY(@ids)", connection);
                    command.Parameters.AddWithValue("ids", workspaceIds);

                    using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    var workspaceNames = new Dictionary<Guid, string>();
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        workspaceNames.Add(reader.GetFieldValue<Guid>(0), reader.GetString(1));
                    }

                    foreach (var dto in dtos)
                    {
                        if (Guid.TryParse(dto.WorkspaceId, out var gId) && workspaceNames.TryGetValue(gId, out var realName))
                        {
                            dto.WorkspaceName = realName;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve workspace names for global invoices from identity schema");
            }

            return Result.Success(new PagedResult<InvoiceDto>(total, dtos));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global invoices");
            return Result.Failure<PagedResult<InvoiceDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}
