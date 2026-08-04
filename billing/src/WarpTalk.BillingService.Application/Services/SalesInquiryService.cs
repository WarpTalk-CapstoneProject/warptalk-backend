using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using System.Linq.Expressions;
using System.Text.Json;


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

    public async Task<Result<SalesInquiryDto>> CreatePublicInquiryAsync(CreateSalesInquiryRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateCreate(request);
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

    public async Task<Result<SalesInquiryDto>> CreateWorkspaceInquiryAsync(
        CreateWorkspaceSalesInquiryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.WorkspaceId == Guid.Empty)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.WorkspaceIdRequired, ErrorCodes.ValidationError);

        var createRequest = request.ToCreateRequest();
        var validation = ValidateCreate(createRequest);
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

    public async Task<Result<PaginatedResponse<SalesInquiryDto>>> GetSalesInquiriesAsync(SalesInquiryQuery query, CancellationToken cancellationToken = default)
    {
        var (page, pageSize, skip) = NormalizePagination(query);
        var normalizedStatus = NormalizeStatus(query.Status);
        var predicate = BuildQueryPredicate(query, normalizedStatus);

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

    public async Task<Result<SalesInquiryDto>> GetSalesInquiryByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var inquiry = await _unitOfWork.SalesInquiryRepository.GetByIdAsync(id, cancellationToken);
        return inquiry is null
            ? Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.NotFound, ErrorCodes.NotFound)
            : Result.Success(inquiry.ToDto());
    }

    public async Task<Result<SalesInquiryDto>> UpdateSalesInquiryStatusAsync(Guid id, UpdateSalesInquiryStatusRequest request, CancellationToken cancellationToken = default)
    {
        var status = NormalizeStatus(request.Status);
        if (status is null)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.StatusInvalid, ErrorCodes.ValidationError);

        var inquiry = await _unitOfWork.SalesInquiryRepository.GetByIdAsync(id, cancellationToken);
        if (inquiry is null)
            return Result.Failure<SalesInquiryDto>(SalesInquiryConstants.Errors.NotFound, ErrorCodes.NotFound);

        var now = DateTime.UtcNow;
        inquiry.Status = status;
        inquiry.UpdatedAt = now;

        if (status == SalesInquiryConstants.Statuses.Converted)
        {
            inquiry.ConvertedAt ??= now;
            inquiry.ClosedAt = null;
        }
        else if (status == SalesInquiryConstants.Statuses.Closed)
        {
            inquiry.ClosedAt ??= now;
        }
        _unitOfWork.SalesInquiryRepository.Update(inquiry);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(inquiry.ToDto());
    }

    public async Task<Result<SalesInquiryDto>> LinkSalesInquiryWorkspaceAsync(
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

    public async Task<Result<SalesInquiryDto>> ConvertSalesInquiryToContractAsync(
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
            ? await _unitOfWork.Plans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive && p.DeletedAt == null, cancellationToken)
            : await _unitOfWork.Plans.FirstOrDefaultAsync(
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
        var convertedNow = DateTime.UtcNow;
        inquiry.Status = SalesInquiryConstants.Statuses.Converted;
        inquiry.UpdatedAt = convertedNow;
        inquiry.ConvertedAt ??= convertedNow;
        inquiry.ClosedAt = null;
        _unitOfWork.SalesInquiryRepository.Update(inquiry);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(inquiry.ToDto());
    }

    private static Result ValidateCreate(CreateSalesInquiryRequest request)
    {
        if (!request.Consent)
            return Result.Failure(SalesInquiryConstants.Errors.ConsentRequired, ErrorCodes.ValidationError);

        if (request.FeatureInterests is null || request.FeatureInterests.Count == 0)
            return Result.Failure(SalesInquiryConstants.Errors.FeatureInterestRequired, ErrorCodes.ValidationError);

        if (request.TargetLanguages is null || request.TargetLanguages.Count == 0)
            return Result.Failure(SalesInquiryConstants.Errors.TargetLanguageRequired, ErrorCodes.ValidationError);

        if (string.IsNullOrWhiteSpace(request.FirstName) ||
            string.IsNullOrWhiteSpace(request.LastName) ||
            string.IsNullOrWhiteSpace(request.WorkEmail) ||
            string.IsNullOrWhiteSpace(request.Company) ||
            string.IsNullOrWhiteSpace(request.RequestType) ||
            string.IsNullOrWhiteSpace(request.CurrentMonthlyMeetingVolume))
        {
            return Result.Failure(SalesInquiryConstants.Errors.RequiredFieldsMissing, ErrorCodes.ValidationError);
        }

        var requestedMonthlyCredits = TryReadLong(request.PricingEstimate, "requestedMonthlyCredits");
        if (requestedMonthlyCredits is { } credits &&
            (credits < 1 || credits > SalesInquiryConstants.Defaults.MaxRequestedMonthlyCredits))
        {
            return Result.Failure(SalesInquiryConstants.Errors.RequestedMonthlyCreditsInvalid, ErrorCodes.ValidationError);
        }

        var requestedWorkspaceMembers = TryReadLong(request.PricingEstimate, "requestedWorkspaceMembers");
        if (requestedWorkspaceMembers is { } members &&
            (members < 1 || members > SalesInquiryConstants.Defaults.MaxRequestedWorkspaceMembers))
        {
            return Result.Failure(SalesInquiryConstants.Errors.RequestedWorkspaceMembersInvalid, ErrorCodes.ValidationError);
        }

        return Result.Success();
    }

    private static (int Page, int PageSize, int Skip) NormalizePagination(SalesInquiryQuery query)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, SalesInquiryConstants.Defaults.MaxPageSize);
        return (page, pageSize, (page - 1) * pageSize);
    }

    private static Expression<Func<WarpTalk.BillingService.Domain.Entities.SalesInquiry, bool>> BuildQueryPredicate(SalesInquiryQuery query, string? normalizedStatus)
    {
        var search = query.Search?.Trim().ToLowerInvariant();
        var workspaceId = query.WorkspaceId;

        return inquiry =>
            (normalizedStatus == null || inquiry.Status == normalizedStatus) &&
            (workspaceId == null || inquiry.WorkspaceId == workspaceId) &&
            (search == null ||
             inquiry.WorkEmail.ToLower().Contains(search) ||
             inquiry.Company.ToLower().Contains(search) ||
             inquiry.FirstName.ToLower().Contains(search) ||
             inquiry.LastName.ToLower().Contains(search));
    }

    private static long? TryReadLong(object? source, string propertyName)
    {
        if (source is null)
            return null;

        if (source is JsonElement element)
            return TryReadLong(element, propertyName);

        if (source is IDictionary<string, object?> dictionary &&
            dictionary.TryGetValue(propertyName, out var value))
        {
            return TryReadLongValue(value);
        }

        try
        {
            return TryReadLong(JsonSerializer.SerializeToElement(source), propertyName);
        }
        catch
        {
            return null;
        }
    }

    private static long? TryReadLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return TryReadLongValue(property);
    }

    private static long? TryReadLongValue(object? value)
    {
        return value switch
        {
            null => null,
            long longValue => longValue,
            int intValue => intValue,
            decimal decimalValue when decimalValue == decimal.Truncate(decimalValue) => (long)decimalValue,
            double doubleValue when doubleValue % 1 == 0 => (long)doubleValue,
            string stringValue when long.TryParse(stringValue, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } text when long.TryParse(text.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        var normalized = status.Trim().ToLowerInvariant();
        return SalesInquiryConstants.Statuses.All.Contains(normalized) ? normalized : null;
    }
}
