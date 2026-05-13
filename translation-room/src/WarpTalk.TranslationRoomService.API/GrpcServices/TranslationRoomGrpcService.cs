using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.GrpcServices;

public class TranslationRoomGrpcService : Shared.Protos.TranslationRoomService.TranslationRoomServiceBase
{
    private readonly ITranslationRoomRepository _repository;

    public TranslationRoomGrpcService(ITranslationRoomRepository repository)
    {
        _repository = repository;
    }

    public override async Task<GetTranslationRoomResponse> GetTranslationRoomById(GetTranslationRoomRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId(TranslationRoomConstants.ErrorMessages.EntityTranslationRoom);

        var translationRoom = await _repository.GetByIdAsync(parsedId);

        if (translationRoom is null)
            throw GrpcErrors.NotFound(TranslationRoomConstants.ErrorMessages.EntityTranslationRoom, request.Id);

        return new GetTranslationRoomResponse
        {
            Id = translationRoom.Id.ToString(),
            Title = translationRoom.Title
        };
    }
}
