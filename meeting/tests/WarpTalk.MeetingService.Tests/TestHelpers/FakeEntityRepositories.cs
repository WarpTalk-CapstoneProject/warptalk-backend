using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Tests.TestHelpers;

// The unit of work hands out a repository interface per table rather than IGenericRepository<T>,
// so the in-memory double needs a type per table too. Each adds nothing beyond the base fake —
// they exist only to satisfy the interface the service asks for.

public sealed class FakeRtcSessionRevocationRepository
    : FakeGenericRepository<RtcSessionRevocation>, IRtcSessionRevocationRepository;
