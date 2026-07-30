namespace WarpTalk.BillingService.API.Services;

public interface IIdempotencyService
{
    Task<string?> GetResponseJsonAsync(string key, string operation, string requestHash, CancellationToken ct = default);

    Task StoreResponseJsonAsync(string key, string operation, string requestHash, string responseJson, Guid? workspaceId = null, CancellationToken ct = default);
}
