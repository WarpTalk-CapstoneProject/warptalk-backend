using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
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

    public async Task<Result<PaginatedResponse<InvoiceDto>>> GetInvoicesAsync(
        Guid workspaceId, PaginationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var size = query.PageSize > 0 ? query.PageSize : 20;
            var skip = ((query.PageNumber > 0 ? query.PageNumber : 1) - 1) * size;

            var items = await _unitOfWork.InvoiceRepository.GetPagedAsync(
                i => i.Payment.Subscription.WorkspaceId == workspaceId,
                skip, size,
                q => q.OrderByDescending(i => i.CreatedAt),
                includes: new System.Linq.Expressions.Expression<Func<Invoice, object>>[] { i => i.Payment, i => i.Payment.Subscription },
                cancellationToken: cancellationToken);

            var total = await _unitOfWork.InvoiceRepository.CountAsync(
                i => i.Payment.Subscription.WorkspaceId == workspaceId,
                cancellationToken);

            var dtos = items.Select(i => i.ToDto(workspaceId)).ToList();
            return Result.Success(PaginatedResponse<InvoiceDto>.Create(dtos, total, query.PageNumber, query.PageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting invoices for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<PaginatedResponse<InvoiceDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PaginatedResponse<InvoiceDto>>> GetGlobalInvoicesAsync(
        PaginationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var size = query.PageSize > 0 ? query.PageSize : 20;
            var skip = ((query.PageNumber > 0 ? query.PageNumber : 1) - 1) * size;

            var items = await _unitOfWork.InvoiceRepository.GetPagedAsync(
                i => true,
                skip, size,
                q => q.OrderByDescending(i => i.CreatedAt),
                includes: new System.Linq.Expressions.Expression<Func<Invoice, object>>[] { i => i.Payment, i => i.Payment.Subscription },
                cancellationToken: cancellationToken);

            var total = await _unitOfWork.InvoiceRepository.CountAsync(i => true, cancellationToken);
            var dtos = items.Select(i => i.ToDto(i.Payment.Subscription.WorkspaceId)).ToList();

            // Resolve workspace names cross-schema
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
                        workspaceNames.Add(reader.GetFieldValue<Guid>(0), reader.GetString(1));

                    foreach (var dto in dtos)
                    {
                        if (Guid.TryParse(dto.WorkspaceId, out var gId) && workspaceNames.TryGetValue(gId, out var realName))
                            dto.WorkspaceName = realName;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve workspace names for global invoices");
            }

            return Result.Success(PaginatedResponse<InvoiceDto>.Create(dtos, total, query.PageNumber, query.PageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global invoices");
            return Result.Failure<PaginatedResponse<InvoiceDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}
