namespace WarpTalk.NotificationService.Application.DTOs.Common;

/// <summary>The outcome of a bulk action for one item. A bulk action never stops at the first failure.</summary>
public sealed record BulkItemResultDto(string Id, bool Succeeded, string? Error);

public sealed record BulkResultDto(IReadOnlyList<BulkItemResultDto> Items)
{
    public int Succeeded => Items.Count(item => item.Succeeded);
    public int Failed => Items.Count(item => !item.Succeeded);
}
