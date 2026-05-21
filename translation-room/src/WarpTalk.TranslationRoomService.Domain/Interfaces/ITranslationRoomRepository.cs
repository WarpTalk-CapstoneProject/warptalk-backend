using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomRepository : IGenericRepository<TranslationRoom>
{
    Task<bool> ExistsByCodeAsync(string roomCode, IEnumerable<string>? excludedStatuses = null, CancellationToken cancellationToken = default);
    Task<TranslationRoom?> GetByCodeAsync(string roomCode, IEnumerable<string>? excludedStatuses = null, CancellationToken cancellationToken = default);
    Task<List<TranslationRoom>> GetHistoryByUserIdAsync(Guid userId, int limit, int offset, CancellationToken ct = default);
}
