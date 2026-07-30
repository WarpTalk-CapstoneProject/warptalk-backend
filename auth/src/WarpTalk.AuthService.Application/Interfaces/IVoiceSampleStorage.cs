using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IVoiceSampleStorage
{
    Task<string> SaveAsync(string storageKey, Stream contentStream, CancellationToken ct = default);
    Task<Stream> ReadAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
}
