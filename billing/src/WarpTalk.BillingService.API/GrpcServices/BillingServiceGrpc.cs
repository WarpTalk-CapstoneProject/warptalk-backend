using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Protos;
using WarpTalk.BillingService.Application.Interfaces;

using WarpTalk.BillingService.Domain.Interfaces;
using Dtos = WarpTalk.BillingService.Application.DTOs;
using Entities = WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.API.GrpcServices;

public partial class BillingServiceGrpc : WarpTalk.Shared.Protos.BillingService.BillingServiceBase
{
    private readonly ICreditService _creditService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IPaymentService _paymentService;
    private readonly IPaymentAppService _paymentAppService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceAuthorizationService _workspaceAuthService;
    private readonly ILogger<BillingServiceGrpc> _logger;

    public BillingServiceGrpc(
        ICreditService creditService, 
        ISubscriptionService subscriptionService,
        IPaymentService paymentService,
        IPaymentAppService paymentAppService,
        IUnitOfWork unitOfWork,
        IWorkspaceAuthorizationService workspaceAuthService,
        ILogger<BillingServiceGrpc> logger)
    {
        _creditService = creditService;
        _subscriptionService = subscriptionService;
        _paymentService = paymentService;
        _paymentAppService = paymentAppService;
        _unitOfWork = unitOfWork;
        _workspaceAuthService = workspaceAuthService;
        _logger = logger;
    }

    private async Task AuthorizeWorkspaceAsync(Guid workspaceId, ServerCallContext context, string allowedRoles = "Owner, Admin")
    {
        var httpContext = context.GetHttpContext();
        var userId = httpContext.User.GetUserId();
        
        if (userId == null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication required"));
        }

        var authResult = await _workspaceAuthService.AuthorizeAsync(workspaceId, userId.Value, allowedRoles, context.CancellationToken);
        if (!authResult.IsSuccess)
        {
            var statusCode = authResult.ErrorCode == "FORBIDDEN" ? StatusCode.PermissionDenied : StatusCode.Internal;
            throw new RpcException(new Status(statusCode, authResult.Error ?? "Access denied"));
        }
    }



    private static SubscriptionResponse MapToSubscriptionResponse(Entities.Subscription sub, string planName)
    {
        return new SubscriptionResponse
        {
            SubscriptionId = sub.Id.ToString(),
            Status = sub.Status.ToString().ToLowerInvariant(),
            ErrorMessage = string.Empty,
            PlanId = sub.PlanId.ToString(),
            PlanName = planName,
            WorkspaceId = sub.WorkspaceId.ToString(),
            CreditsRemaining = sub.CreditsRemaining,
            CurrentPeriodStart = sub.CurrentPeriodStart.ToString("o"),
            CurrentPeriodEnd = sub.CurrentPeriodEnd.ToString("o"),
            AutoRenew = sub.AutoRenew,
            CancelledAt = sub.CancelledAt?.ToString("o") ?? string.Empty
        };
    }
    
    private static SubscriptionResponse MapToSubscriptionResponse(Dtos.SubscriptionDto dto)
    {
        return new SubscriptionResponse
        {
            SubscriptionId = dto.Id.ToString(),
            Status = dto.Status.ToLowerInvariant(),
            ErrorMessage = string.Empty,
            PlanId = dto.PlanId.ToString(),
            PlanName = dto.PlanName,
            WorkspaceId = dto.WorkspaceId.ToString(),
            CreditsRemaining = dto.CreditsRemaining,
            CurrentPeriodStart = dto.CurrentPeriodStart.ToString("o"),
            CurrentPeriodEnd = dto.CurrentPeriodEnd.ToString("o"),
            AutoRenew = dto.AutoRenew,
            CancelledAt = dto.CancelledAt?.ToString("o") ?? string.Empty
        };
    }
}
