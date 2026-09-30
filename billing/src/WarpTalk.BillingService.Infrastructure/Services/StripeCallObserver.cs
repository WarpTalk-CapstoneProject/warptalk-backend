using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stripe;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>
/// Every HTTP call billing makes to Stripe, counted by outcome and timed to its response headers,
/// for the admin Providers page's Stripe live rate, error rate, latency and 90-day row.
///
/// WHERE IT SITS
///   An HttpMessageHandler under the one <see cref="StripeClient"/> the whole service uses
///   (StripeConfiguration.StripeClient, set in Program) and under StripeFxClient's own client — so
///   checkout sessions, the G11 catalog sync (products, prices, coupons, promotion codes),
///   subscriptions, invoices, charges, balance transactions and FX quotes all pass through it, and a
///   new Stripe call cannot be added around it. Stripe.net's own retries are separate calls here.
///
/// OUTCOMES (the provider_call_stats vocabulary)
///   2xx/3xx ok · 402 card_error → declined (the issuer said no; Stripe worked) · other 402 quota ·
///   401/403 auth · 429 rate_limited · other 4xx client_error (a request WE got wrong:
///   invalid_request_error, idempotency_error) · 5xx server_error · timeout · network_error.
/// </summary>
public sealed class StripeCallObserver : DelegatingHandler
{
    public const string Provider = ProviderCatalog.Stripe;

    /// <summary>A resource name (checkout, payment_intents). Ids carry digits or capitals (prod_Q1w2, cs_test_a1B2).</summary>
    private static readonly Regex ResourceSegment = new(@"^[a-z_]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProviderCallRecorder _recorder;

    public StripeCallObserver(IProviderCallRecorder recorder, HttpMessageHandler? inner = null)
        : base(inner ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        _recorder = recorder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var operation = OperationOf(request.Method, request.RequestUri);
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Our own cancellation says nothing about Stripe.
            throw;
        }
        catch (Exception ex)
        {
            _recorder.Record(Provider, operation, ClassifyException(ex), Elapsed(started));
            throw;
        }

        var outcome = ClassifyStatus(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.PaymentRequired && response.Content is not null)
        {
            // Stripe's error bodies are small JSON; buffering one leaves it readable for the SDK.
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            outcome = ClassifyPaymentRequired(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        _recorder.Record(Provider, operation, outcome, Elapsed(started));
        return response;
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>"POST /v1/checkout/sessions" → checkout.sessions.post; ids (prod_…, cs_…) are dropped.</summary>
    public static string OperationOf(HttpMethod method, Uri? uri)
    {
        var segments = (uri?.AbsolutePath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != "v1" && segment != "v2" && ResourceSegment.IsMatch(segment))
            .Take(3)
            .Select(segment => segment.ToLowerInvariant());
        var path = string.Join('.', segments);
        return (path.Length == 0 ? "root" : path) + "." + method.Method.ToLowerInvariant();
    }

    public static string ClassifyStatus(HttpStatusCode status)
    {
        var code = (int)status;
        if (code < 400) return "ok";
        if (code == 402) return "quota";
        if (code == 429) return "rate_limited";
        if (code is 401 or 403) return "auth";
        if (code >= 500) return "server_error";
        return "client_error";
    }

    /// <summary>A 402 is a declined card when Stripe says card_error; anything else is the account.</summary>
    public static string ClassifyPaymentRequired(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "quota";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "card_error")
            {
                return "declined";
            }
        }
        catch (JsonException)
        {
        }

        return "quota";
    }

    public static string ClassifyException(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "timeout",
        HttpRequestException or IOException => "network_error",
        _ => "error",
    };

    /// <summary>The Stripe.net HTTP client over this handler, for a <see cref="StripeClient"/>.</summary>
    public static IHttpClient CreateStripeHttpClient(IProviderCallRecorder recorder)
        => new SystemNetHttpClient(
            new HttpClient(new StripeCallObserver(recorder)) { Timeout = TimeSpan.FromSeconds(80) },
            maxNetworkRetries: StripeConfiguration.MaxNetworkRetries);
}
