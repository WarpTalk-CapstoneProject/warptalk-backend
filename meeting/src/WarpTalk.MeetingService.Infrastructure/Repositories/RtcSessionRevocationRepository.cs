using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class RtcSessionRevocationRepository : GenericRepository<RtcSessionRevocation>, IRtcSessionRevocationRepository
{
    public RtcSessionRevocationRepository(MeetingDbContext context) : base(context)
    {
    }
}
