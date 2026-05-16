using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.GrpcServices;

public class TranslationRoomGrpcService : Shared.Protos.TranslationRoomService.TranslationRoomServiceBase
{
    private readonly ITranslationRoomService _translationRoomService;

    public TranslationRoomGrpcService(ITranslationRoomService translationRoomService)
    {
        _translationRoomService = translationRoomService;
    }

    public override async Task<GetTranslationRoomResponse> GetTranslationRoomById(GetTranslationRoomRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId(TranslationRoomConstants.EntityTranslationRoom);

        var result = await _translationRoomService.GetTranslationRoomAsync(parsedId, context.CancellationToken);

        if (!result.IsSuccess)
            throw GrpcErrors.NotFound(TranslationRoomConstants.EntityTranslationRoom, request.Id);

        return new GetTranslationRoomResponse
        {
            Id = result.Value!.Id.ToString(),
            Title = result.Value!.Title
        };
    }
}
