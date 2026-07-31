using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class SalesInquiryService : ISalesInquiryService
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(SalesInquiryConstants.Defaults.DuplicateWindowMinutes);

    private readonly IUnitOfWork _unitOfWork;
    private readonly ISubscriptionService _subscriptionService;

    public SalesInquiryService(IUnitOfWork unitOfWork, ISubscriptionService subscriptionService)
    {
        _unitOfWork = unitOfWork;
        _subscriptionService = subscriptionService;
    }

    public async Task<Result<SalesInquiryDto>> CreateAsync(CreateSalesInquiryRequest request, CancellationToken cancellationToken = default)
    {
        var validation = SalesInquiryHelper.ValidateCreate(request);
        if (!validation.IsSuccess)
            return Result.Failure<SalesInquiryDto>(validation.Error!, validation.ErrorCode);

        var email = request.WorkEmail.Trim().ToLowerInvariant();
        var company = request.Company.Trim().ToLowerInvariant();
        var duplicateAfter = DateTime.UtcNow.Subtract(DuplicateWindow);
        var existing = await _unitOfWork.SalesInquiryRepository.FirstOrDefaultAsync(
            i => i.WorkEmail == email &&
                 i.Company.ToLower() == company &&
                 i.CreatedAt >= duplicateAfter &&
                 i.Status != SalesInquiryConstants.Statuses.Closed,
            cancellationToken);

        if (existing is not null)
            return Result.Success(existing.ToDto());

        var inquiry = request.ToEntity();
        await _unitOfWork.SalesInquiryRepository.AddAsync(inquiry, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(inquiry.ToDto());
    }

    public async Task<Result<SalesInquiryDto>> CreateWorkspaceAsync(
        CreateWorkspaceSalesInquiryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.WorkspaceId == Guid.Empty)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.WorkspaceIdRequired, ErrorCodes.ValidationError);

        var createRequest = request.ToCreateRequest();
        var validation = SalesInquiryHelper.ValidateCreate(createRequest);
        if (!validation.IsSuccess)
            return Result.Failure<SalesInquiryDto>(validation.Error!, validation.ErrorCode);

        var email = request.WorkEmail.Trim().ToLowerInvariant();
        var company = request.Company.Trim().ToLowerInvariant();
        var duplicateAfter = DateTime.UtcNow.Subtract(DuplicateWindow);
        var existing = await _unitOfWork.SalesInquiryRepository.FirstOrDefaultAsync(
            i => i.WorkEmail == email &&
                 i.Company.ToLower() == company &&
                 i.CreatedAt >= duplicateAfter &&
                 i.Status != SalesInquiryConstants.Statuses.Closed,
            cancellationToken);

        if (existing is not null)
        {
            if (existing.WorkspaceId is null)
            {
                existing.WorkspaceId = request.WorkspaceId;
                existing.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.SalesInquiryRepository.Update(existing);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return Result.Success(existing.ToDto());
        }

        var inquiry = request.ToWorkspaceEntity();
        await _unitOfWork.SalesInquiryRepository.AddAsync(inquiry, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(inquiry.ToDto());
    }

    public async Task<Result<PaginatedResponse<SalesInquiryDto>>> GetAsync(SalesInquiryQuery query, CancellationToken cancellationToken = default)
    {
        var (page, pageSize, skip) = SalesInquiryHelper.NormalizePagination(query);
        var normalizedStatus = SalesInquiryHelper.NormalizeStatus(query.Status);
        var predicate = SalesInquiryHelper.BuildQueryPredicate(query, normalizedStatus);

        var totalCount = await _unitOfWork.SalesInquiryRepository.CountAsync(predicate, cancellationToken);
        var pageItems = await _unitOfWork.SalesInquiryRepository.GetPagedAsync(
            predicate,
            skip,
            pageSize,
            q => q
                .OrderBy(i =>
                    i.Status == SalesInquiryConstants.Statuses.New ? 0 :
                    i.Status == SalesInquiryConstants.Statuses.Reviewing ? 1 :
                    i.Status == SalesInquiryConstants.Statuses.Quoted ? 2 :
                    i.Status == SalesInquiryConstants.Statuses.Converted ? 3 :
                    i.Status == SalesInquiryConstants.Statuses.Closed ? 4 :
                    99)
                .ThenByDescending(i => i.CreatedAt),
            cancellationToken);

        var dtos = pageItems
            .Select(i => i.ToDto())
            .ToList();

        return Result.Success(PaginatedResponse<SalesInquiryDto>.Create(dtos, totalCount, page, pageSize));
    }

    public async Task<Result<SalesInquiryDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var inquiry = await _unitOfWork.SalesInquiryRepository.GetByIdAsync(id, cancellationToken);
        return inquiry is null
            ? Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.NotFound, ErrorCodes.NotFound)
            : Result.Success(inquiry.ToDto());
    }

    public async Task<Result<SalesInquiryDto>> UpdateStatusAsync(Guid id, UpdateSalesInquiryStatusRequest request, CancellationToken cancellationToken = default)
    {
        var status = SalesInquiryHelper.NormalizeStatus(request.Status);
        if (status is null)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.StatusInvalid, ErrorCodes.ValidationError);

        var inquiry = await _unitOfWork.SalesInquiryRepository.GetByIdAsync(id, cancellationToken);
        if (inquiry is null)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.NotFound, ErrorCodes.NotFound);

        SalesInquiryHelper.ApplyStatus(inquiry, status);
        _unitOfWork.SalesInquiryRepository.Update(inquiry);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(inquiry.ToDto());
    }

    public async Task<Result<SalesInquiryDto>> LinkWorkspaceAsync(
        Guid id,
        LinkSalesInquiryWorkspaceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.WorkspaceId == Guid.Empty)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.WorkspaceIdRequired, ErrorCodes.ValidationError);

        var inquiry = await _unitOfWork.SalesInquiryRepository.GetByIdAsync(id, cancellationToken);
        if (inquiry is null)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.NotFound, ErrorCodes.NotFound);

        inquiry.WorkspaceId = request.WorkspaceId;
        inquiry.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.SalesInquiryRepository.Update(inquiry);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(inquiry.ToDto());
    }

    public async Task<Result<SalesInquiryDto>> ConvertToContractAsync(
        Guid id,
        ConvertSalesInquiryToContractRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.WorkspaceId == Guid.Empty)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.WorkspaceIdRequired, ErrorCodes.ValidationError);

        var inquiry = await _unitOfWork.SalesInquiryRepository.GetByIdAsync(id, cancellationToken);
        if (inquiry is null)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.NotFound, ErrorCodes.NotFound);

        var plan = request.PlanId is Guid planId && planId != Guid.Empty
            ? await _unitOfWork.PlanRepository.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive && p.DeletedAt == null, cancellationToken)
            : await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Slug == SubscriptionConstants.PlanSlugs.Enterprise && p.IsActive && p.DeletedAt == null,
                cancellationToken);

        if (plan is null)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.EnterprisePlanNotFound, ErrorCodes.BillingPlanNotFound);

        var terms = request.ToContractTermsWithBillingContact(inquiry);

        var activeSubscription = await _subscriptionService.GetActiveSubscriptionAsync(request.WorkspaceId, cancellationToken);
        Result<SubscriptionDto> subscriptionResult;
        if (activeSubscription.IsSuccess)
        {
            if (activeSubscription.Value!.PlanId == plan.Id && activeSubscription.Value!.TrialEndsAt == null)
            {
                subscriptionResult = await _subscriptionService.UpdateContractTermsAsync(request.WorkspaceId, terms, cancellationToken);
            }
            else
            {
                // Deactivate the trial (or old) subscription immediately and start a new contract subscription
                await _subscriptionService.CancelSubscriptionAsync(request.WorkspaceId, SalesInquiryConstants.Messages.ConvertedToNewContract, cancellationToken);
                subscriptionResult = await _subscriptionService.CreateWorkspaceContractSubscriptionAsync(
                    request.ToContractSubscriptionRequest(plan.Id, terms),
                    cancellationToken);
            }
        }
        else
        {
            subscriptionResult = await _subscriptionService.CreateWorkspaceContractSubscriptionAsync(
                request.ToContractSubscriptionRequest(plan.Id, terms),
                cancellationToken);
        }

        if (!subscriptionResult.IsSuccess)
            return Result.Failure<SalesInquiryDto>(subscriptionResult.Error!, subscriptionResult.ErrorCode);

        inquiry.WorkspaceId = request.WorkspaceId;
        inquiry.SubscriptionId = subscriptionResult.Value!.Id;
        SalesInquiryHelper.ApplyStatus(inquiry, SalesInquiryConstants.Statuses.Converted);
        _unitOfWork.SalesInquiryRepository.Update(inquiry);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(inquiry.ToDto());
    }
}
