namespace WarpTalk.BillingService.Application.Interfaces;

public interface IWorkspaceDirectory
{
    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(
        IEnumerable<Guid> workspaceIds,
        CancellationToken cancellationToken = default);
}
