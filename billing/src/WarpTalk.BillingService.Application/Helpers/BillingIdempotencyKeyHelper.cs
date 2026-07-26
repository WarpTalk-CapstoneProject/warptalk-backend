using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Helpers;

public static class BillingIdempotencyKeyHelper
{
    private const string AggregatePrefix = "AGG";
    private const string UsagePrefix = "USAGE";
    private const string DirectPrefix = "DIRECT";

    public static string ForAggregate(IEnumerable<string?> sourceKeys)
    {
        var orderedKeys = sourceKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        return Build(AggregatePrefix, orderedKeys);
    }

    public static string ForUsage(RecordUsageRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return request.IdempotencyKey;

        return Build(UsagePrefix, new
        {
            request.HostWorkspaceId,
            request.UserId,
            request.UsageType,
            request.Unit,
            request.Quantity,
            request.CreditsConsumed,
            request.DurationSeconds,
            request.TranslationRoomId,
            request.SegmentId,
            request.Details
        });
    }

    public static string ForDirectConsume(Guid workspaceId, ConsumeCreditsRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return request.IdempotencyKey;

        return Build(DirectPrefix, new
        {
            WorkspaceId = workspaceId,
            request.Amount,
            request.ReferenceType,
            request.ReferenceId
        });
    }

    private static string Build(string prefix, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return $"{prefix}:{hash}";
    }
}
