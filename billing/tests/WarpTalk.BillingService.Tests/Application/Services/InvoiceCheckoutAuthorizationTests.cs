using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// POST /api/v1/invoices/{invoiceId}/checkout — the last WT-260 offender.
///
/// It authorised off JWT role claims, so a real workspace Owner (never a claim) got 403, and a
/// platform-role holder could open checkout on any workspace's invoice. The route names only an
/// invoice, so the role is checked against the workspace the invoice belongs to, after lookup.
/// </summary>
public class InvoiceCheckoutAuthorizationTests
{
    private static readonly Guid InvoiceId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid InvoiceRecipient = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Caller = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private const string CheckoutUrl = "https://checkout.stripe.com/c/pay/cs_test_invoice";

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IInvoiceRepository> _invoices = new();
    private readonly Mock<IStripePaymentService> _stripe = new();
    private readonly Mock<IWorkspaceClient> _workspaceClient = new();
    private CreateCheckoutSessionRequest? _sentToStripe;

    public InvoiceCheckoutAuthorizationTests()
    {
        _unitOfWork.Setup(u => u.InvoiceRepository).Returns(_invoices.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _stripe
            .Setup(s => s.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateCheckoutSessionRequest, CancellationToken>((request, _) => _sentToStripe = request)
            .ReturnsAsync(Result.Success(CheckoutUrl));
    }

    [Fact]
    public async Task AWorkspaceOwner_GetsACheckoutUrl_AndIsTheBuyer()
    {
        GivenInvoice(InvoiceConstants.InvoiceStatuses.Open);
        GivenCallerHasOwnerRole(true);

        var result = await Service().CreateInvoiceCheckoutSessionAsync(
            InvoiceId, new InvoiceCheckoutCaller(Caller, "owner@warptalk.io.vn", false));

        Assert.True(result.IsSuccess);
        Assert.Equal(CheckoutUrl, result.Value);
        Assert.NotNull(_sentToStripe);
        Assert.Equal(Caller, _sentToStripe!.UserId);
        Assert.Equal("owner@warptalk.io.vn", _sentToStripe.BuyerEmail);
        Assert.Equal(WorkspaceId, _sentToStripe.WorkspaceId);
        _workspaceClient.Verify(c => c.VerifyWorkspaceRolesAsync(
            WorkspaceId, Caller, WorkspaceRoleConstants.Owner), Times.Once);
    }

    [Fact]
    public async Task ACallerWithoutTheOwnerRole_IsForbidden_AndStripeIsNeverCalled()
    {
        GivenInvoice(InvoiceConstants.InvoiceStatuses.Open);
        GivenCallerHasOwnerRole(false);

        var result = await Service().CreateInvoiceCheckoutSessionAsync(
            InvoiceId, new InvoiceCheckoutCaller(Caller, "", false));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Null(_sentToStripe);
    }

    [Fact]
    public async Task AWorkspaceServiceFailure_IsNotReportedAsForbidden()
    {
        GivenInvoice(InvoiceConstants.InvoiceStatuses.Open);
        _workspaceClient
            .Setup(c => c.VerifyWorkspaceRolesAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string[]>()))
            .ReturnsAsync(Result.Failure<bool>("workspace-service unavailable", ErrorCodes.InternalServerError));

        var result = await Service().CreateInvoiceCheckoutSessionAsync(
            InvoiceId, new InvoiceCheckoutCaller(Caller, "", false));

        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
        Assert.Null(_sentToStripe);
    }

    [Fact]
    public async Task APlatformAdmin_SkipsTheWorkspaceLookup()
    {
        GivenInvoice(InvoiceConstants.InvoiceStatuses.Open);

        var result = await Service().CreateInvoiceCheckoutSessionAsync(
            InvoiceId, new InvoiceCheckoutCaller(Caller, "", true));

        Assert.True(result.IsSuccess);
        _workspaceClient.Verify(c => c.VerifyWorkspaceRolesAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string[]>()), Times.Never);
    }

    [Theory]
    [InlineData(InvoiceConstants.InvoiceStatuses.Void)]
    [InlineData(InvoiceConstants.InvoiceStatuses.Uncollectible)]
    [InlineData(InvoiceConstants.InvoiceStatuses.Paid)]
    public async Task AnInvoiceThatIsNotOwed_CannotBePaid(string status)
    {
        GivenInvoice(status);
        GivenCallerHasOwnerRole(true);

        var result = await Service().CreateInvoiceCheckoutSessionAsync(
            InvoiceId, new InvoiceCheckoutCaller(Caller, "", false));

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Null(_sentToStripe);
    }

    [Fact]
    public async Task AMissingInvoice_IsNotFound()
    {
        _invoices
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Invoice, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Invoice?)null);

        var result = await Service().CreateInvoiceCheckoutSessionAsync(
            InvoiceId, new InvoiceCheckoutCaller(Caller, "", false));

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    private void GivenInvoice(string status)
    {
        var invoice = new Invoice
        {
            Id = InvoiceId,
            UserId = InvoiceRecipient,
            InvoiceNumber = "INV-1",
            Total = 12.5m,
            Currency = "usd",
            Status = status,
            LineItems = "[]",
            Payment = new Payment
            {
                Currency = "usd",
                PaymentMethod = PaymentConstants.PaymentMethods.Invoice,
                Provider = PaymentConstants.Providers.InternalInvoice,
                Subscription = new Subscription { WorkspaceId = WorkspaceId },
            },
        };

        _invoices
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Invoice, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invoice);
    }

    private void GivenCallerHasOwnerRole(bool isOwner)
    {
        _workspaceClient
            .Setup(c => c.VerifyWorkspaceRolesAsync(WorkspaceId, Caller, It.IsAny<string[]>()))
            .ReturnsAsync(Result.Success(isOwner));
    }

    private InvoiceService Service() => new(
        _unitOfWork.Object,
        Mock.Of<ILogger<InvoiceService>>(),
        _stripe.Object,
        _workspaceClient.Object);
}
