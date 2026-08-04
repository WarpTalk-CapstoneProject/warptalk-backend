using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Tests.TestHelpers;

// The unit of work hands out a repository interface per table rather than IGenericRepository<T>,
// so the in-memory double needs a type per table too. Each adds nothing beyond the base fake —
// they exist only to satisfy the interface the service asks for.

public sealed class FakeBreakoutSessionRepository
    : FakeGenericRepository<BreakoutSession>, IBreakoutSessionRepository;

public sealed class FakeBreakoutAssignmentRepository
    : FakeGenericRepository<BreakoutAssignment>, IBreakoutAssignmentRepository;

public sealed class FakePollRepository
    : FakeGenericRepository<Poll>, IPollRepository;

public sealed class FakePollOptionRepository
    : FakeGenericRepository<PollOption>, IPollOptionRepository;

public sealed class FakePollVoteRepository
    : FakeGenericRepository<PollVote>, IPollVoteRepository;

public sealed class FakeQuestionRepository
    : FakeGenericRepository<Question>, IQuestionRepository;

public sealed class FakeQuestionVoteRepository
    : FakeGenericRepository<QuestionVote>, IQuestionVoteRepository;

public sealed class FakeMeetingInvitationRepository
    : FakeGenericRepository<MeetingInvitation>, IMeetingInvitationRepository;
