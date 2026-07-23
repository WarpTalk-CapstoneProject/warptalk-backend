using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface ICreditTransactionRepository : IGenericRepository<CreditTransaction>
{
    Task<Dictionary<Guid, string>> GetWorkspaceNamesAsync(IEnumerable<Guid> workspaceIds, CancellationToken cancellationToken = default);
}
