using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// Where receipts of operating expenses are kept (G12): the platform's S3-compatible object storage
/// (Storage:S3, the in-cluster MinIO in production), a local folder in Development. When neither is
/// configured <see cref="IsAvailable"/> is false and uploads are refused with a clear message instead
/// of the billing service failing to start.
/// </summary>
public interface IExpenseReceiptStorage
{
    bool IsAvailable { get; }

    Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>The object's bytes, or null when it is gone.</summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default);

    Task DeleteAsync(string storageKey, CancellationToken ct = default);
}
