using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.API.Controllers;

/// <summary>
/// WT-545 — who the buyer is, is decided by the token.
///
/// The buyer id used to arrive in the request BODY and was written straight onto the Stripe
/// session's metadata. That metadata is what GetAndProcessCheckoutSessionAsync trusts when it
/// waves a caller through the workspace-role check ("this is the buyer"), so a request that
/// named somebody else as the buyer minted a session that person — not the payer — was
/// authorised to complete. The downstream check can only mean something if the value it reads
/// was never the client's to choose.
/// </summary>
public class CheckoutBuyerIdentityTests
{
    private static readonly Guid TokenUserId = Guid.Parse("8f1c5b30-1c7a-4d2e-9a11-0f7e2c4b6a01");
    private static readonly Guid SomebodyElse = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid WorkspaceId = Guid.Parse("b21d4f88-3c66-4f5a-9d0e-77c1a9f43e02");
    private const string TokenEmail = "buyer@warptalk.io.vn";

    [Fact]
    public async Task TheBuyerOnTheSession_IsTheCaller_NotWhoeverTheBodyNamed()
    {
        var (controller, appService) = ControllerWith(TokenUserId, TokenEmail);
        CreateCheckoutSessionRequest? forwarded = null;
        appService
            .Setup(service => service.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>()))
            .Callback<CreateCheckoutSessionRequest>(request => forwarded = request)
            .ReturnsAsync(Result.Success("https://checkout.stripe.com/c/pay/cs_test_1"));

        // A request that claims a different buyer — exactly the spoof the ticket describes.
        await controller.CreateCheckoutSession(
            new CreateCheckoutSessionRequest(SomebodyElse, WorkspaceId, 10m));

        Assert.NotNull(forwarded);
        Assert.Equal(TokenUserId, forwarded!.UserId);
    }

    [Fact]
    public async Task TheBuyersEmail_TravelsWithTheSession_SoStripeBindsThePageToTheirAccount()
    {
        var (controller, appService) = ControllerWith(TokenUserId, TokenEmail);
        CreateCheckoutSessionRequest? forwarded = null;
        appService
            .Setup(service => service.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>()))
            .Callback<CreateCheckoutSessionRequest>(request => forwarded = request)
            .ReturnsAsync(Result.Success("https://checkout.stripe.com/c/pay/cs_test_1"));

        await controller.CreateCheckoutSession(
            new CreateCheckoutSessionRequest(TokenUserId, WorkspaceId, 10m));

        Assert.Equal(TokenEmail, forwarded!.BuyerEmail);
    }

    /// <summary>
    /// A client-sent BuyerEmail would put an arbitrary address on the payment page and on the
    /// receipt, which is the same forgery one level down. It is overwritten, not merged.
    /// </summary>
    [Fact]
    public async Task ABuyerEmailSentByTheClient_IsDiscarded()
    {
        var (controller, appService) = ControllerWith(TokenUserId, TokenEmail);
        CreateCheckoutSessionRequest? forwarded = null;
        appService
            .Setup(service => service.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>()))
            .Callback<CreateCheckoutSessionRequest>(request => forwarded = request)
            .ReturnsAsync(Result.Success("https://checkout.stripe.com/c/pay/cs_test_1"));

        await controller.CreateCheckoutSession(
            new CreateCheckoutSessionRequest(TokenUserId, WorkspaceId, 10m)
            {
                BuyerEmail = "attacker@example.com",
            });

        Assert.Equal(TokenEmail, forwarded!.BuyerEmail);
    }

    [Fact]
    public async Task AnUnauthenticatedCaller_GetsNoSessionAtAll()
    {
        var (controller, appService) = ControllerWith(userId: null, email: null);

        var result = await controller.CreateCheckoutSession(
            new CreateCheckoutSessionRequest(SomebodyElse, WorkspaceId, 10m));

        Assert.IsType<UnauthorizedResult>(result);
        appService.Verify(
            service => service.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>()),
            Times.Never);
    }

    private static (PaymentsController, Mock<IPaymentAppService>) ControllerWith(Guid? userId, string? email)
    {
        var appService = new Mock<IPaymentAppService>();
        var controller = new PaymentsController(
            new Mock<IPaymentService>().Object,
            appService.Object,
            new Mock<IStripeWebhookService>().Object,
            new Mock<IWorkspaceClient>().Object);

        var claims = new List<Claim>();
        if (userId != null) claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        if (email != null) claims.Add(new Claim(ClaimTypes.Email, email));

        var identity = userId == null
            ? new ClaimsIdentity()
            : new ClaimsIdentity(claims, authenticationType: "TestJwt");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        return (controller, appService);
    }
}
