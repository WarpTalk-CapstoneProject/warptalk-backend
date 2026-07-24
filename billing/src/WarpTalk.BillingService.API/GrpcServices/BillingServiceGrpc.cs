using System;
using WarpTalk.BillingService.Domain.Constants;
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

}
