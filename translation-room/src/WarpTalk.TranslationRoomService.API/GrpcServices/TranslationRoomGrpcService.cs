using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure;

namespace WarpTalk.TranslationRoomService.API.GrpcServices;

public class TranslationRoomGrpcService : Shared.Protos.TranslationRoomService.TranslationRoomServiceBase
{
    private readonly IUnitOfWork _unitOfWork;

    public TranslationRoomGrpcService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public override async Task<GetTranslationRoomResponse> GetTranslationRoomById(GetTranslationRoomRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId("TranslationRoom");

        var repo = _unitOfWork.Repository<TranslationRoom>();
        var translationRoom = await repo.GetByIdAsync(parsedId);

        if (translationRoom is null)
            throw GrpcErrors.NotFound("TranslationRoom", request.Id);

        return new GetTranslationRoomResponse
        {
            Id = translationRoom.Id.ToString(),
            Title = translationRoom.Title
        };
    }
}
