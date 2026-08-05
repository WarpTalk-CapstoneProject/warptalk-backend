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
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _participantRepository;

    public TranslationRoomGrpcService(
        ITranslationRoomService translationRoomService,
        ITranslationRoomRepository translationRoomRepository,
        ITranslationRoomParticipantRepository participantRepository)
    {
        _translationRoomService = translationRoomService;
        _translationRoomRepository = translationRoomRepository;
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
            WorkspaceId = result.Value!.WorkspaceId.ToString(),
            Title = result.Value!.Title,
            Description = result.Value!.Description ?? string.Empty,
            HostId = result.Value!.HostId.ToString(),
            Status = result.Value!.Status.ToString(),
            StartedAt = result.Value!.StartedAt?.ToString("O") ?? string.Empty,
            EndedAt = result.Value!.EndedAt?.ToString("O") ?? string.Empty,
            ScheduledStartTime = result.Value!.ScheduledAt?.ToString("O") ?? string.Empty
        };
    }

    public override async Task<GetParticipantsByRoomIdResponse> GetParticipantsByRoomId(GetParticipantsByRoomIdRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.RoomId, out var parsedRoomId))
            throw GrpcErrors.InvalidId(TranslationRoomConstants.EntityTranslationRoom);

        var participants = await _participantRepository.FindAsync(p => p.TranslationRoomId == parsedRoomId, "", context.CancellationToken);

        var response = new GetParticipantsByRoomIdResponse();

        // WT-263: the roster's IsActive is the same question the WT-262 capacity cap asks, so it
        // answers from the shared seat definition instead of its own status literal. Safe as a method
        // call because FindAsync has already materialised the rows — this loop is LINQ-to-Objects,
        // not an EF predicate (the cap uses SeatHolding.Contains, which is what translates to SQL).
        foreach (var p in participants)
        {
            response.Participants.Add(new Shared.Protos.Participant
            {
                Id = p.UserId?.ToString() ?? string.Empty,
                DisplayName = p.DisplayName ?? string.Empty,
                Role = p.Role ?? string.Empty,
                Language = p.SpeakLanguage ?? string.Empty,
                IsActive = TranslationRoomParticipantStatuses.HoldsSeat(p.Status)
            });
        }

        return response;
    }

    public override async Task<GetActiveRoomCountByWorkspaceResponse> GetActiveRoomCountByWorkspace(
        GetActiveRoomCountByWorkspaceRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId("Workspace");

        var count = await _translationRoomRepository.CountActiveByWorkspaceAsync(
            workspaceId,
            context.CancellationToken);
        return new GetActiveRoomCountByWorkspaceResponse { Count = count };
    }
}
