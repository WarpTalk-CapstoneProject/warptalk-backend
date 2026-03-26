using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure;

namespace WarpTalk.MeetingService.API.GrpcServices;

public class MeetingGrpcService : Shared.Protos.MeetingService.MeetingServiceBase
{
    private readonly IUnitOfWork _unitOfWork;

    public MeetingGrpcService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public override async Task<GetMeetingResponse> GetMeetingById(GetMeetingRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId("Meeting");

        var repo = _unitOfWork.Repository<Meeting>();
        var meeting = await repo.GetByIdAsync(parsedId);

        if (meeting is null)
            throw GrpcErrors.NotFound("Meeting", request.Id);

        return new GetMeetingResponse
        {
            Id = meeting.Id.ToString(),
            Title = meeting.Title
        };
    }
}
