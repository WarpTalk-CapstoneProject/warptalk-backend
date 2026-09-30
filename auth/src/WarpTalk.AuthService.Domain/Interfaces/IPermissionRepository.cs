using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IPermissionRepository : IGenericRepository<Permission>
{
    /// <summary>Active permissions whose code is in the list.</summary>
    Task<IReadOnlyList<Permission>> GetByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct = default);
}
