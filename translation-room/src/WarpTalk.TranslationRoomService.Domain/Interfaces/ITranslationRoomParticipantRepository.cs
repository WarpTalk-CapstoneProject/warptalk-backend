<<<<<<< HEAD
using System;
using System.Collections.Generic;
=======
>>>>>>> 80e45ad1325ea4819c4e38a4a5b6fa5c95549e8d
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomParticipantRepository : IGenericRepository<TranslationRoomParticipant>
{
    Task<TranslationRoomParticipant?> GetByRoomAndUserAsync(Guid roomId, Guid userId, CancellationToken cancellationToken = default);
<<<<<<< HEAD
    Task<List<TranslationRoomParticipant>> GetByRoomIdAsync(Guid roomId, CancellationToken ct = default);
=======
>>>>>>> 80e45ad1325ea4819c4e38a4a5b6fa5c95549e8d
}
