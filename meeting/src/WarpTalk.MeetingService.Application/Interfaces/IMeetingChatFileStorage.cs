using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingChatFileStorage
{
    Task SaveAsync(string storageKey, Stream contentStream, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
}
