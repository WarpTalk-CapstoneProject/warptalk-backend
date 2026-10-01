using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Stripe;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>
/// Stripe's FX data, through the billing service's own Stripe.net (51.1.0).
///
/// WHAT STRIPE OFFERS (checked against the production account, a US sandbox settling in USD, 2026-09-24)
///   - The Exchange Rates API is gone: GET /v1/exchange_rates/usd answers "Unrecognized request URL",
///     and Stripe.net 51 has no ExchangeRateService.
///   - The FX Quotes API (POST /v1/fx_quotes) answers, but only with a <c>.preview</c> Stripe-Version header,
///     and Stripe.net has no service for it — so it goes through <see cref="StripeClient.RawRequestAsync"/>,
///     the SDK's documented door for preview endpoints (same key, retries and telemetry). A quote with
///     <c>lock_duration=none</c> locks nothing and costs nothing. <c>rates.usd.rate_details.base_rate</c> is
///     the rate before Stripe's 1% FX fee; <c>rates.usd.exchange_rate</c> is after it.
///   - Every VND charge's balance transaction carries the <c>exchange_rate</c> Stripe applied converting it
///     into the USD balance, per MINOR unit (1 VND → 0.0038 US cents), so VND per dollar = 100 / rate.
///     That dates past days, back to the first converted charge.
/// </summary>
public sealed class StripeFxClient : IStripeFxClient
{
    /// <summary>The preview API version the FX Quotes API answered with. Override with <c>Stripe:FxQuotesApiVersion</c>.</summary>
    public const string DefaultFxQuotesApiVersion = "2025-09-30.preview";

    public const int MaxChargePages = 20;

    private readonly string _apiKey;
    private readonly string _fxQuotesApiVersion;
    private readonly IHttpClient? _httpClient;
    private StripeClient? _client;

    public StripeFxClient(IConfiguration configuration)
        : this(configuration["Stripe:SecretKey"], configuration["Stripe:FxQuotesApiVersion"], httpClient: null)
    {
    }

    /// <summary>For tests: a fake <see cref="IHttpClient"/> sees exactly what Stripe would.</summary>
    public StripeFxClient(string? apiKey, string? fxQuotesApiVersion, IHttpClient? httpClient)
    {
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _fxQuotesApiVersion = string.IsNullOrWhiteSpace(fxQuotesApiVersion) ? DefaultFxQuotesApiVersion : fxQuotesApiVersion.Trim();
        _httpClient = httpClient;
    }

    public bool IsConfigured => _apiKey.Length > 0;

    private StripeClient Client => _client ??= new StripeClient(apiKey: _apiKey, httpClient: _httpClient);

    public async Task<StripeFxQuote> GetUsdToVndQuoteAsync(CancellationToken ct = default)
    {
        var response = await Client.RawRequestAsync(
            HttpMethod.Post,
            "/v1/fx_quotes",
            "to_currency=vnd&from_currencies[]=usd&lock_duration=none",
            new RawRequestOptions { AdditionalHeaders = { ["Stripe-Version"] = _fxQuotesApiVersion } },
            ct);

        if ((int)response.StatusCode >= 400)
        {
            throw new InvalidOperationException($"Stripe FX quote answered HTTP {(int)response.StatusCode}: {ErrorMessage(response.Content)}");
        }

        return ParseQuote(response.Content);
    }

    /// <summary>Parses a USD→VND fx_quote body. Public for tests.</summary>
    public static StripeFxQuote ParseQuote(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var usd = root.GetProperty("rates").GetProperty("usd");
        var details = usd.GetProperty("rate_details");
        var baseRate = details.GetProperty("base_rate").GetDecimal();
        var exchangeRate = usd.GetProperty("exchange_rate").GetDecimal();
        decimal? fee = details.TryGetProperty("fx_fee_rate", out var feeRate) && feeRate.ValueKind == JsonValueKind.Number
            ? feeRate.GetDecimal()
            : null;
        var created = root.TryGetProperty("created", out var createdAt) && createdAt.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(createdAt.GetInt64()).UtcDateTime
            : DateTime.UtcNow;
        return new StripeFxQuote(root.GetProperty("id").GetString() ?? string.Empty, created, baseRate, exchangeRate, fee);
    }

    public async Task<IReadOnlyList<StripeChargeConversion>> GetVndChargeConversionsAsync(DateTime since, CancellationToken ct = default)
    {
        var service = new BalanceTransactionService(Client);
        var result = new List<StripeChargeConversion>();
        string? after = null;

        for (var page = 0; page < MaxChargePages; page++)
        {
            var options = new BalanceTransactionListOptions
            {
                Type = "charge",
                Limit = 100,
                Created = new DateRangeOptions { GreaterThanOrEqual = DateTime.SpecifyKind(since, DateTimeKind.Utc) },
                StartingAfter = after,
            };
            options.AddExpand("data.source");

            var list = await service.ListAsync(options, cancellationToken: ct);
            foreach (var transaction in list.Data)
            {
                if (ToConversion(transaction) is { } conversion) result.Add(conversion);
            }

            if (!list.HasMore || list.Data.Count == 0) break;
            after = list.Data[^1].Id;
        }

        return result;
    }

    /// <summary>
    /// A VND charge settled into USD: VND per US dollar from the minor-unit exchange rate. Anything
    /// else (a USD charge, no conversion) is not a USD→VND data point. Public for tests.
    /// </summary>
    public static StripeChargeConversion? ToConversion(BalanceTransaction transaction)
    {
        if (transaction.ExchangeRate is not { } rate || rate <= 0) return null;
        if (!string.Equals(transaction.Currency, "usd", StringComparison.OrdinalIgnoreCase)) return null;
        if (transaction.Source is not Charge charge || !string.Equals(charge.Currency, "vnd", StringComparison.OrdinalIgnoreCase)) return null;

        // VND has no minor unit, USD has cents: amount_vnd × rate = amount_cents.
        return new StripeChargeConversion(
            transaction.Id,
            DateTime.SpecifyKind(transaction.Created, DateTimeKind.Utc),
            100m / rate);
    }

    private static string ErrorMessage(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
            {
                var text = message.GetString() ?? string.Empty;
                return text.Length > 200 ? text[..200] : text;
            }
        }
        catch (JsonException)
        {
        }

        return "no error message";
    }
}
