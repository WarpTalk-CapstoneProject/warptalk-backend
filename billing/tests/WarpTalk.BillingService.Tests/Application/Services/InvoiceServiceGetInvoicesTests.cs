using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// Function 59 – View Invoices: <see cref="InvoiceService.GetInvoicesAsync"/>.
/// The 403 case (UTCID02) lives in
/// <see cref="WarpTalk.BillingService.Tests.API.Controllers.WorkspaceInvoiceAuthorizationTests.GetWorkspaceInvoices_NonMember_IsForbiddenWithErrorBody"/>.
/// </summary>
public class InvoiceServiceGetInvoicesTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IInvoiceRepository> _invoiceRepository = new();
    private readonly Mock<ILogger<InvoiceService>> _logger = new();
    private readonly InvoiceService _service;

    public InvoiceServiceGetInvoicesTests()
    {
        _unitOfWork.Setup(u => u.InvoiceRepository).Returns(_invoiceRepository.Object);

        _service = new InvoiceService(
            _unitOfWork.Object,
            _logger.Object,
            Mock.Of<IStripePaymentService>(),
            Mock.Of<IWorkspaceClient>());
    }

    [Fact]
    public async Task GetInvoicesAsync_UTCID01_InvoicesExist_ReturnsPaginatedDtos()
    {
        var invoices = new[] { CreateInvoice("INV-1", 100m), CreateInvoice("INV-2", 200m) };
        SetupPage(new PagedResult<Invoice>(invoices, TotalCount: 12, PageNumber: 1, PageSize: 10));

        var result = await _service.GetInvoicesAsync(WorkspaceId, new PaginationQuery(1, 10));

        result.IsSuccess.Should().BeTrue();
        var page = result.Value!;
        page.PageNumber.Should().Be(1);
        page.PageSize.Should().Be(10);
        page.TotalCount.Should().Be(12);
        page.TotalPages.Should().Be(2);
        page.HasNextPage.Should().BeTrue();
        page.Items.Select(i => i.InvoiceNumber).Should().Equal("INV-1", "INV-2");
        page.Items[0].Id.Should().Be(invoices[0].Id.ToString());
        page.Items[1].Total.Should().Be(200m);
        page.Items[0].Status.Should().Be(InvoiceConstants.InvoiceStatuses.Paid.ToLower());

        _invoiceRepository.Verify(
            r => r.GetPageAsync(new PageRequest(1, 10), WorkspaceId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The service and BillingQueryHelper pass the raw page/pageSize through; clamping happens in
    /// the repository via RepositoryPaging.Normalize (page >= 1, pageSize 1..200). This drives the
    /// real (internal) normalizer and feeds its output back as the repository's answer.
    /// </summary>
    [Theory]
    [InlineData(0, 10, 1, 10)]
    [InlineData(-5, 0, 1, 1)]
    [InlineData(2, -3, 2, 1)]
    [InlineData(1, 500, 1, 200)]
    public async Task GetInvoicesAsync_UTCID03_InvalidPaging_IsNormalizedByRepositoryPaging(
        int requestedPage, int requestedSize, int expectedPage, int expectedSize)
    {
        var (normalizedPage, normalizedSize, skip) = InvokeRepositoryPagingNormalize(new PageRequest(requestedPage, requestedSize));
        normalizedPage.Should().Be(expectedPage);
        normalizedSize.Should().Be(expectedSize);
        skip.Should().Be((expectedPage - 1) * expectedSize);

        PageRequest? passed = null;
        _invoiceRepository
            .Setup(r => r.GetPageAsync(It.IsAny<PageRequest>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<PageRequest, Guid?, CancellationToken>((p, _, _) => passed = p)
            .ReturnsAsync(new PagedResult<Invoice>(Array.Empty<Invoice>(), 0, normalizedPage, normalizedSize));

        var result = await _service.GetInvoicesAsync(WorkspaceId, new PaginationQuery(requestedPage, requestedSize));

        // Service does not normalize: raw values reach the repository.
        passed.Should().Be(new PageRequest(requestedPage, requestedSize));
        result.IsSuccess.Should().BeTrue();
        result.Value!.PageNumber.Should().Be(expectedPage);
        result.Value.PageSize.Should().Be(expectedSize);
    }

    [Fact]
    public async Task GetInvoicesAsync_UTCID04_PageBeyondAvailable_ReturnsEmptyPageSuccessfully()
    {
        SetupPage(new PagedResult<Invoice>(Array.Empty<Invoice>(), TotalCount: 3, PageNumber: 5, PageSize: 10));

        var result = await _service.GetInvoicesAsync(WorkspaceId, new PaginationQuery(5, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(3);
        result.Value.PageNumber.Should().Be(5);
        result.Value.TotalPages.Should().Be(1);
        result.Value.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public async Task GetInvoicesAsync_UTCID05_ScopesRepositoryQueryToRequestedWorkspace()
    {
        SetupPage(new PagedResult<Invoice>(new[] { CreateInvoice("INV-OWN", 50m) }, 1, 1, 10));

        var result = await _service.GetInvoicesAsync(WorkspaceId, new PaginationQuery(1, 10));

        result.IsSuccess.Should().BeTrue();
        // The workspace filter is the repository's (Payment.Subscription.WorkspaceId == workspaceId);
        // the service must hand it the workspace id, never null (the unscoped global listing).
        _invoiceRepository.Verify(
            r => r.GetPageAsync(It.IsAny<PageRequest>(), It.Is<Guid?>(w => w == WorkspaceId), It.IsAny<CancellationToken>()),
            Times.Once);
        _invoiceRepository.Verify(
            r => r.GetPageAsync(It.IsAny<PageRequest>(), null, It.IsAny<CancellationToken>()),
            Times.Never);
        result.Value!.Items.Should().OnlyContain(i => i.WorkspaceId == WorkspaceId.ToString());
    }

    [Fact]
    public async Task GetInvoicesAsync_UTCID06_RepositoryThrows_ReturnsInternalErrorAndLogs()
    {
        _invoiceRepository
            .Setup(r => r.GetPageAsync(It.IsAny<PageRequest>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _service.GetInvoicesAsync(WorkspaceId, new PaginationQuery(1, 10));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
        result.Error.Should().Be(ApiMessageConstants.ErrorMessages.BillingInternalError);
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(WorkspaceId.ToString())),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private void SetupPage(PagedResult<Invoice> page) =>
        _invoiceRepository
            .Setup(r => r.GetPageAsync(It.IsAny<PageRequest>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

    private static (int PageNumber, int PageSize, int Skip) InvokeRepositoryPagingNormalize(PageRequest request)
    {
        var pagingType = typeof(InvoiceRepository).Assembly
            .GetType("WarpTalk.BillingService.Infrastructure.Repositories.RepositoryPaging", throwOnError: true)!;
        var normalize = pagingType.GetMethod("Normalize", BindingFlags.Public | BindingFlags.Static)!;
        var normalized = normalize.Invoke(null, new object[] { request })!;
        var type = normalized.GetType();
        return (
            (int)type.GetProperty("PageNumber")!.GetValue(normalized)!,
            (int)type.GetProperty("PageSize")!.GetValue(normalized)!,
            (int)type.GetProperty("Skip")!.GetValue(normalized)!);
    }

    private static Invoice CreateInvoice(string number, decimal total) => new()
    {
        Id = Guid.NewGuid(),
        PaymentId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        InvoiceNumber = number,
        Subtotal = total,
        Tax = 0,
        Total = total,
        Currency = PaymentConstants.Currencies.Usd,
        Status = InvoiceConstants.InvoiceStatuses.Paid,
        LineItems = "[]",
        IssuedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };
}
