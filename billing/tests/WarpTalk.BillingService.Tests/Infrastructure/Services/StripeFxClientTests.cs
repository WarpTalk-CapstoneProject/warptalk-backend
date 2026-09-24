using System.Net;
using FluentAssertions;
using Stripe;
using WarpTalk.BillingService.Infrastructure.Services;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

/// <summary>
/// Stripe's FX data, as the production sandbox returned it on 2026-09-24 (usd→vnd base 25,989.9, 1% fee;
/// a 100,000 VND charge settled as 384 US cents at exchange_rate 0.00384478).
/// </summary>
public sealed class StripeFxClientTests
{
    private const string QuoteJson = """
        {"id":"fxq_1UJCLDDeCPz3gizYjW3moA4d","object":"fx_quote","created":1790255135,"livemode":false,
         "lock_duration":"none","lock_expires_at":null,"lock_status":"none",
         "rates":{"usd":{"exchange_rate":25730.0,"rate_details":{"base_rate":25989.9,"duration_premium":0.0,
         "fx_fee_rate":0.01,"reference_rate":null,"reference_rate_provider":null}}},"to_currency":"vnd"}
        """;

    [Fact]
    public void Quote_takes_the_fee_exclusive_base_rate()
    {
        var quote = StripeFxClient.ParseQuote(QuoteJson);

        quote.Id.Should().Be("fxq_1UJCLDDeCPz3gizYjW3moA4d");
        quote.BaseRate.Should().Be(25_989.9m);
        quote.ExchangeRate.Should().Be(25_730m);
        quote.FxFeeRate.Should().Be(0.01m);
        quote.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790255135).UtcDateTime);
    }

    [Fact]
    public async Task Quote_is_asked_with_the_preview_version_header_the_API_requires()
    {
        var http = new RecordingHttpClient(QuoteJson);
        var client = new StripeFxClient("sk_test_x", null, http);

        var quote = await client.GetUsdToVndQuoteAsync();

        quote.BaseRate.Should().Be(25_989.9m);
        http.Request!.Method.Should().Be(HttpMethod.Post);
        http.Request.Uri.AbsolutePath.Should().Be("/v1/fx_quotes");
        http.Request.StripeHeaders["Stripe-Version"].Should().Be(StripeFxClient.DefaultFxQuotesApiVersion);
        var body = await http.Request.Content!.ReadAsStringAsync();
        Uri.UnescapeDataString(body).Should().Contain("to_currency=vnd").And.Contain("from_currencies[]=usd").And.Contain("lock_duration=none");
    }

    [Fact]
    public void A_converted_VND_charge_gives_VND_per_dollar_from_the_minor_unit_rate()
    {
        var conversion = StripeFxClient.ToConversion(new BalanceTransaction
        {
            Id = "txn_1",
            Currency = "usd",
            Amount = 384,
            ExchangeRate = 0.00384478m,
            Created = new DateTime(2026, 9, 23, 11, 49, 59, DateTimeKind.Utc),
            Source = new Charge { Amount = 100_000, Currency = "vnd" },
        });

        conversion.Should().NotBeNull();
        Math.Round(conversion!.VndPerUsd, 1).Should().Be(26_009.3m);
    }

    [Fact]
    public void A_charge_that_was_not_converted_from_VND_is_not_a_data_point()
    {
        StripeFxClient.ToConversion(new BalanceTransaction { Currency = "usd", Source = new Charge { Currency = "usd" } }).Should().BeNull();
        StripeFxClient.ToConversion(new BalanceTransaction { Currency = "usd", ExchangeRate = 0.9m, Source = new Charge { Currency = "eur" } }).Should().BeNull();
    }

    [Fact]
    public void Without_a_key_it_says_so_instead_of_calling_Stripe()
    {
        new StripeFxClient(" ", null, null).IsConfigured.Should().BeFalse();
    }

    private sealed class RecordingHttpClient(string body) : IHttpClient
    {
        public StripeRequest? Request { get; private set; }

        public Task<StripeResponse> MakeRequestAsync(StripeRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new StripeResponse(HttpStatusCode.OK, null, body));
        }

        public Task<StripeStreamedResponse> MakeStreamingRequestAsync(StripeRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
