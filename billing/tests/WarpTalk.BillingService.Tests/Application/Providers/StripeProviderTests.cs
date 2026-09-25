using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Stripe;
using Stripe.Checkout;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.Shared;
using static WarpTalk.BillingService.Application.Services.ProviderMetricsCalculator;

namespace WarpTalk.BillingService.Tests.Application.Providers;

/// <summary>
/// The Stripe card of the admin Providers page (owner, 2026-09-25: "Stripe status doesn't work, no
/// data"): billing's own Stripe calls, inbound webhooks, and Stripe's fee as the cost line.
/// </summary>
public sealed class StripeProviderTests
{
    private sealed class FakeRecorder : IProviderCallRecorder
    {
        public readonly List<(string Provider, string Operation, string Outcome, long? LatencyMs)> Calls = [];

        public void Record(string provider, string operation, string outcome, long? latencyMs, string? model = null)
            => Calls.Add((provider, operation, outcome, latencyMs));
    }

    private sealed class Stub(HttpStatusCode status, string body = "{}", Exception? throws = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throws is not null) throw throws;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    // ── recording our Stripe calls ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("POST", "https://api.stripe.com/v1/checkout/sessions", "checkout.sessions.post")]
    [InlineData("GET", "https://api.stripe.com/v1/checkout/sessions/cs_test_a1B2c3", "checkout.sessions.get")]
    [InlineData("POST", "https://api.stripe.com/v1/products/prod_Q1w2E3r4", "products.post")]
    [InlineData("GET", "https://api.stripe.com/v1/payment_intents/pi_3Abc", "payment_intents.get")]
    [InlineData("GET", "https://api.stripe.com/v1/charges?payment_intent=pi_1", "charges.get")]
    [InlineData("POST", "https://api.stripe.com/v1/fx_quotes", "fx_quotes.post")]
    public void An_operation_is_the_resource_and_method_never_an_id(string method, string url, string operation)
        => StripeCallObserver.OperationOf(new HttpMethod(method), new Uri(url)).Should().Be(operation);

    [Theory]
    [InlineData(HttpStatusCode.OK, "ok")]
    [InlineData(HttpStatusCode.BadRequest, "client_error")]
    [InlineData(HttpStatusCode.NotFound, "client_error")]
    [InlineData(HttpStatusCode.Unauthorized, "auth")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "server_error")]
    public void A_status_names_whose_problem_it_is(HttpStatusCode status, string outcome)
        => StripeCallObserver.ClassifyStatus(status).Should().Be(outcome);

    [Fact]
    public async Task A_declined_card_is_recorded_as_declined_and_the_sdk_can_still_read_the_error()
    {
        var recorder = new FakeRecorder();
        var body = """{"error":{"type":"card_error","code":"card_declined","message":"Your card was declined."}}""";
        using var client = new HttpClient(new StripeCallObserver(recorder, new Stub(HttpStatusCode.PaymentRequired, body)));

        using var response = await client.PostAsync("https://api.stripe.com/v1/payment_intents/pi_3Abc/confirm", new StringContent(""));

        recorder.Calls.Should().ContainSingle().Which.Outcome.Should().Be("declined");
        (await response.Content.ReadAsStringAsync()).Should().Be(body);
    }

    [Fact]
    public async Task A_network_failure_is_recorded_and_still_thrown()
    {
        var recorder = new FakeRecorder();
        using var client = new HttpClient(new StripeCallObserver(recorder, new Stub(HttpStatusCode.OK, throws: new HttpRequestException("reset"))));

        var act = () => client.GetAsync("https://api.stripe.com/v1/products");

        await act.Should().ThrowAsync<HttpRequestException>();
        recorder.Calls.Should().ContainSingle().Which.Should().Match<(string, string Operation, string Outcome, long?)>(c => c.Outcome == "network_error" && c.Operation == "products.get");
    }

    [Fact]
    public void Another_402_is_the_account_not_the_card()
        => StripeCallObserver.ClassifyPaymentRequired("""{"error":{"type":"invalid_request_error"}}""").Should().Be("quota");

    [Fact]
    public void The_field_layout_is_the_one_the_ai_workers_write_and_the_sync_parses()
    {
        var at = new DateTime(2026, 9, 25, 7, 42, 0, DateTimeKind.Utc);

        // warptalk-ai tests/test_provider_calls.py pins the same three strings.
        ProviderCallStatsParser.FieldsFor("OpenAI", "translation", "gpt-4.1", "ok", 730, at).Should().Equal(
            ("openai|07|translation|gpt-4.1|ok", 1L),
            ("openai|07|translation|gpt-4.1|lat:1000", 1L),
            ("openai|07|translation|gpt-4.1|lat_sum", 730L));

        var row = ProviderCallStatsParser.Parse(
            DateOnly.FromDateTime(at),
            ProviderCallStatsParser.FieldsFor("stripe", "checkout.sessions.post", null, "declined", 20001, at)
                .Select(f => new KeyValuePair<string, long>(f.Field, f.Increment))).Single();
        row.Declined.Should().Be(1);
        row.Model.Should().Be("-");
        WarpTalk.BillingService.Domain.Services.ProviderCallStatMerge.ParseBuckets(row.LatencyBuckets).Should().ContainKey("+Inf");
    }

    // ── inbound webhooks ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_webhook_delivery_is_counted_by_outcome()
    {
        var recorder = new FakeRecorder();
        var webhooks = new Mock<IStripeWebhookService>();
        webhooks.SetupSequence(w => w.HandleWebhookAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true))
            .ReturnsAsync(Result.Failure<bool>("db down", ErrorCodes.InternalServerError))
            .ThrowsAsync(new StripeException("No signatures found matching the expected signature"));
        var controller = new PaymentsController(
            new Mock<IPaymentService>().Object, new Mock<IPaymentAppService>().Object, webhooks.Object,
            new Mock<IWorkspaceClient>().Object, calls: recorder);

        foreach (var _ in Enumerable.Range(0, 3))
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
            await controller.Webhook();
        }

        recorder.Calls.Select(c => (c.Operation, c.Outcome)).Should().Equal(
            (WebhookOperation, "ok"), (WebhookOperation, "error"), (WebhookOperation, "auth"));
    }

    // ── what the page makes of it ──────────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 9, 25, 10, 30, 0, DateTimeKind.Utc);

    private static ProviderInputs Stripe(
        IReadOnlyList<ProviderCallStat>? calls = null,
        IReadOnlyList<ProviderPaymentRow>? payments = null,
        IReadOnlyList<PaymentFeeRow>? fees = null)
        => new(ProviderCatalog.Stripe, Now, FxRateTable.Flat(25_000m), [], [], 0m, calls ?? [], Now.AddDays(-2),
            null, null, null, payments ?? [], fees ?? []);

    private static ProviderCallStat Call(string operation, long ok = 0, long declined = 0, long server = 0, long client = 0) => new()
    {
        Provider = ProviderCatalog.Stripe, HourStart = HourOf(Now.AddHours(-1)), Operation = operation, Model = "-",
        Ok = ok, Declined = declined, ServerError = server, ClientError = client,
    };

    private static ProviderCallStat WebhookCall(long ok, long auth)
    {
        var call = Call(WebhookOperation, ok: ok);
        call.Auth = auth;
        return call;
    }

    [Fact]
    public void Declined_cards_are_stripe_working_and_webhooks_never_move_the_success_rate()
    {
        var input = Stripe(calls:
        [
            Call("checkout.sessions.post", ok: 90, declined: 5, server: 5),
            WebhookCall(ok: 3, auth: 7),
        ]);

        var figures = Window(input, Atoms(input, Now.AddDays(-1), Now.AddHours(1)), Now.AddDays(-1), Now.AddHours(1));

        figures.SuccessRate.Should().Be(95m);
        figures.Failures.Should().Be(5);
        figures.Declined.Should().Be(5);
        figures.Webhooks.Should().Be(10);
        figures.WebhookFailures.Should().Be(7);
        StatusOfCalls(95, 5).Should().Be(Degraded);
    }

    [Fact]
    public void Stripe_cost_is_its_fees_in_their_settlement_currency_converted_both_ways()
    {
        var paid = Now.AddHours(-3);
        var input = Stripe(
            payments:
            [
                new ProviderPaymentRow(paid, PaymentConstants.PaymentStatuses.Paid, "VND", 500_000m, Guid.NewGuid()),
                new ProviderPaymentRow(paid, PaymentConstants.PaymentStatuses.Paid, "USD", 20m, Guid.NewGuid()),
            ],
            fees:
            [
                new PaymentFeeRow(Guid.NewGuid(), paid, PaymentProviderFee.StatusOk, "VND", 25_000m),
                new PaymentFeeRow(Guid.NewGuid(), paid, PaymentProviderFee.StatusOk, "USD", 0.88m),
            ]);

        var figures = Window(input, Atoms(input, Now.AddDays(-1), Now.AddHours(1)), Now.AddDays(-1), Now.AddHours(1));

        figures.CostUsd.Should().Be(1.88m);
        figures.CostVnd.Should().Be(47_000m);
        NoteOf(input, figures, Metrics.CostUsd).Should().Contain("balance transaction");
    }

    [Fact]
    public void Paid_payments_with_no_fee_read_yet_are_an_unknown_cost_not_zero()
    {
        var input = Stripe(payments: [new ProviderPaymentRow(Now.AddHours(-2), PaymentConstants.PaymentStatuses.Paid, "VND", 1m, null)]);

        var figures = Window(input, Atoms(input, Now.AddDays(-1), Now.AddHours(1)), Now.AddDays(-1), Now.AddHours(1));

        figures.CostUsd.Should().BeNull();
        NoteOf(input, figures, Metrics.CostUsd).Should().Contain("none has been read");
        MetricsOf(ProviderCatalog.Stripe).Select(m => m.Key).Should().Contain([Metrics.Calls, Metrics.ErrorRate, Metrics.P95, Metrics.CostVnd]);
    }

    // ── reading the fee ────────────────────────────────────────────────────────────────────────

    private static StripeList<Charge> Charges(params Charge[] charges) => new() { Data = [.. charges] };

    [Fact]
    public async Task A_checkout_session_fee_is_read_from_its_charge_balance_transaction_in_major_units()
    {
        var stripe = new Mock<IStripeSdkClient>();
        stripe.Setup(s => s.GetCheckoutSessionAsync("cs_test_1", It.IsAny<CancellationToken>())).ReturnsAsync(new Session { PaymentIntentId = "pi_1" });
        stripe.Setup(s => s.ListChargesAsync(It.Is<ChargeListOptions>(o => o.PaymentIntent == "pi_1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Charges(new Charge
            {
                Id = "ch_1", Status = "succeeded",
                BalanceTransaction = new BalanceTransaction { Id = "txn_1", Currency = "vnd", Amount = 500_000, Fee = 25_000, Net = 475_000, Created = Now },
            }));

        var fee = await new StripeFeeReader(stripe.Object).ReadAsync(Guid.NewGuid(), "cs_test_1", Now, CancellationToken.None);

        fee.Status.Should().Be(PaymentProviderFee.StatusOk);
        fee.Currency.Should().Be("VND");
        fee.Fee.Should().Be(25_000m, "VND is zero-decimal");
        fee.BalanceTransactionId.Should().Be("txn_1");
        StripeFeeReader.Major(88, "USD").Should().Be(0.88m);
    }

    [Fact]
    public async Task A_subscription_invoice_is_followed_through_its_invoice_payment()
    {
        var stripe = new Mock<IStripeSdkClient>();
        stripe.Setup(s => s.ListInvoicePaymentsAsync(It.Is<InvoicePaymentListOptions>(o => o.Invoice == "in_1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeList<InvoicePayment> { Data = [new InvoicePayment { Status = "paid", Payment = new InvoicePaymentPayment { PaymentIntentId = "pi_9" } }] });
        stripe.Setup(s => s.ListChargesAsync(It.Is<ChargeListOptions>(o => o.PaymentIntent == "pi_9"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Charges(new Charge
            {
                Id = "ch_9", Status = "succeeded",
                BalanceTransaction = new BalanceTransaction { Id = "txn_9", Currency = "usd", Amount = 2000, Fee = 88, Net = 1912, Created = Now },
            }));

        var fee = await new StripeFeeReader(stripe.Object).ReadAsync(Guid.NewGuid(), "in_1", Now, CancellationToken.None);

        fee.Fee.Should().Be(0.88m);
        fee.Net.Should().Be(19.12m);
    }

    [Fact]
    public async Task No_charge_is_final_and_a_stripe_error_is_retried_later()
    {
        var stripe = new Mock<IStripeSdkClient>();
        stripe.Setup(s => s.ListChargesAsync(It.IsAny<ChargeListOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(Charges());
        (await new StripeFeeReader(stripe.Object).ReadAsync(Guid.NewGuid(), "pi_1", Now, CancellationToken.None))
            .Status.Should().Be(PaymentProviderFee.StatusNotFound);

        stripe.Setup(s => s.ListChargesAsync(It.IsAny<ChargeListOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(HttpStatusCode.TooManyRequests, new StripeError { Type = "rate_limit_error" }, "slow down"));
        var failed = await new StripeFeeReader(stripe.Object).ReadAsync(Guid.NewGuid(), "pi_1", Now, CancellationToken.None);
        failed.Status.Should().Be(PaymentProviderFee.StatusError);
        failed.Error.Should().Be("Stripe 429: rate_limit_error");
    }
}
