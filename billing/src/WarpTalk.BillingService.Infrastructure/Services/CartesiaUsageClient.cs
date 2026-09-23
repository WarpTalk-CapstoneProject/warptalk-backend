using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Infrastructure.Options;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>One day (or other bucket) of Cartesia credits.</summary>
public sealed record CartesiaCreditBucket(DateTime StartTs, DateTime EndTs, long Credits);

/// <summary>
/// One dimension value of a grouped answer (a capability, a model, …), or — for an ungrouped
/// request — the single series <c>all</c> of flat daily buckets.
/// </summary>
public sealed record CartesiaCreditSeries(string Id, string? Label, IReadOnlyList<CartesiaCreditBucket> Buckets);

public enum CartesiaUsageOutcome
{
    Ok,
    /// <summary>401/403: the key is wrong, revoked, or not an ADMIN key.</summary>
    Unauthorized,
    RateLimited,
    Failed,
}

public sealed record CartesiaCreditUsage(
    CartesiaUsageOutcome Outcome,
    IReadOnlyList<CartesiaCreditSeries> Series,
    int? StatusCode = null,
    TimeSpan? RetryAfter = null)
{
    public static CartesiaCreditUsage Failure(CartesiaUsageOutcome outcome, int? status, TimeSpan? retryAfter = null)
        => new(outcome, Array.Empty<CartesiaCreditSeries>(), status, retryAfter);
}

public interface ICartesiaUsageClient
{
    /// <summary>
    /// <c>GET /usage/credits</c> for the UTC days <paramref name="fromDate"/> .. <paramref name="toDate"/>
    /// inclusive, with <c>interval=day</c>. <paramref name="groupBy"/> is null (flat totals, one
    /// series <c>all</c>), <c>capability</c> or <c>model</c>.
    /// </summary>
    Task<CartesiaCreditUsage> GetCreditsAsync(DateOnly fromDate, DateOnly toDate, string? groupBy, CancellationToken ct);
}

/// <summary>
/// Cartesia's admin usage API (https://docs.cartesia.ai/api-reference/usage/credits). Verified
/// 2026-09-18: buckets are UTC days at the finest (interval = day | week | month; grouped answers
/// must use day), start_ts is rounded down and end_ts up to a UTC midnight, a window may not exceed a
/// year, and the API has no endpoint for the remaining credit balance.
/// </summary>
public sealed class CartesiaUsageClient : ICartesiaUsageClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CartesiaUsageOptions _options;

    public CartesiaUsageClient(IHttpClientFactory httpClientFactory, IOptions<CartesiaUsageOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<CartesiaCreditUsage> GetCreditsAsync(
        DateOnly fromDate, DateOnly toDate, string? groupBy, CancellationToken ct)
    {
        var query = new List<string>
        {
            "start_ts=" + Uri.EscapeDataString(Rfc3339(fromDate)),
            // Exclusive: the next UTC midnight, which Cartesia does not round further.
            "end_ts=" + Uri.EscapeDataString(Rfc3339(toDate.AddDays(1))),
            "interval=day",
        };
        if (groupBy is not null) query.Add("group_by=" + Uri.EscapeDataString(groupBy));
        if (_options.IsFilteredToApiKey) query.Add("api_key_id=" + Uri.EscapeDataString(_options.UsageApiKeyId!.Trim()));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            _options.BaseUrl.TrimEnd('/') + "/usage/credits?" + string.Join('&', query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AdminApiKey!.Trim());
        request.Headers.TryAddWithoutValidation("Cartesia-Version", _options.ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var client = _httpClientFactory.CreateClient(CartesiaUsageOptions.HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var status = (int)response.StatusCode;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return CartesiaCreditUsage.Failure(CartesiaUsageOutcome.Unauthorized, status);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return CartesiaCreditUsage.Failure(CartesiaUsageOutcome.RateLimited, status, RetryAfterOf(response));

        if (!response.IsSuccessStatusCode)
            return CartesiaCreditUsage.Failure(CartesiaUsageOutcome.Failed, status);

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return new CartesiaCreditUsage(CartesiaUsageOutcome.Ok, Parse(document.RootElement), status);
    }

    /// <summary>
    /// Both documented shapes: flat <c>{start_ts, end_ts, credits}</c> buckets (no group_by) become one
    /// series <c>all</c>; grouped <c>{id, label?, buckets}</c> entries become one series each.
    /// </summary>
    public static IReadOnlyList<CartesiaCreditSeries> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Cartesia usage response has no data array.");

        var flat = new List<CartesiaCreditBucket>();
        var series = new List<CartesiaCreditSeries>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("buckets", out var buckets))
            {
                var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var label = item.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String
                    ? labelElement.GetString()
                    : null;
                series.Add(new CartesiaCreditSeries(id, label, buckets.EnumerateArray().Select(ParseBucket).ToList()));
            }
            else
            {
                flat.Add(ParseBucket(item));
            }
        }

        if (flat.Count > 0) series.Insert(0, new CartesiaCreditSeries("all", null, flat));
        return series;
    }

    private static CartesiaCreditBucket ParseBucket(JsonElement bucket)
    {
        var start = bucket.GetProperty("start_ts").GetDateTimeOffset().UtcDateTime;
        var end = bucket.GetProperty("end_ts").GetDateTimeOffset().UtcDateTime;
        var creditsElement = bucket.GetProperty("credits");
        // Documented as an integer; a fractional value is rounded rather than rejected.
        var credits = creditsElement.TryGetInt64(out var whole)
            ? whole
            : (long)Math.Round(creditsElement.GetDecimal(), MidpointRounding.AwayFromZero);
        return new CartesiaCreditBucket(start, end, Math.Max(0, credits));
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static string Rfc3339(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z";
}
