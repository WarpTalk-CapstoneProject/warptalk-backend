using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.Shared.Interfaces;

public interface IEmailTemplateProvider
{
    Task<string> GetTemplateAsync(string templateName, CancellationToken ct = default);
}
