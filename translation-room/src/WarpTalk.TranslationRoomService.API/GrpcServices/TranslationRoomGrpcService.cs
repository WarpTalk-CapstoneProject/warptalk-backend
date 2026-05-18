using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.GrpcServices;

public class TranslationRoomGrpcService : Shared.Protos.TranslationRoomService.TranslationRoomServiceBase
{
    private readonly ITranslationRoomService _translationRoomService;
    private readonly ITranslationRoomParticipantRepository _participantRepository;

    public TranslationRoomGrpcService(
        ITranslationRoomService translationRoomService,
        ITranslationRoomParticipantRepository participantRepository)
    {
        _translationRoomService = translationRoomService;
        _participantRepository = participantRepository;
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
            Title = result.Value!.Title,
            HostId = result.Value!.HostId.ToString(),
            Status = result.Value!.Status.ToString()
        };
    }

    public override async Task<GetParticipantsByRoomIdResponse> GetParticipantsByRoomId(GetParticipantsByRoomIdRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.RoomId, out var parsedRoomId))
            throw GrpcErrors.InvalidId(TranslationRoomConstants.EntityTranslationRoom);

        var participants = await _participantRepository.FindAsync(p => p.TranslationRoomId == parsedRoomId, "", context.CancellationToken);

        var response = new GetParticipantsByRoomIdResponse();
        
        foreach (var p in participants)
        {
            response.Participants.Add(new Shared.Protos.Participant
            {
                Id = p.UserId?.ToString() ?? string.Empty,
                DisplayName = p.DisplayName ?? string.Empty,
                Role = p.Role ?? string.Empty,
                Language = p.SpeakLanguage ?? string.Empty,
                IsActive = p.Status == Domain.Enums.TranslationRoomParticipantStatus.CONNECTED
            });
        }

        return response;
    }
}
