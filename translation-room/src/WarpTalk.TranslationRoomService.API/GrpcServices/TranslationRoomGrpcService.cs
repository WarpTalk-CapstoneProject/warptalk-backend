using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.GrpcServices;

public class TranslationRoomGrpcService : Shared.Protos.TranslationRoomService.TranslationRoomServiceBase
{
    private readonly ITranslationRoomService _translationRoomService;
    private readonly ITranslationRoomDirectoryService _directoryService;

    public TranslationRoomGrpcService(
        ITranslationRoomService translationRoomService,
        ITranslationRoomDirectoryService directoryService)
    {
        _translationRoomService = translationRoomService;
        _directoryService = directoryService;
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

        var result = await _directoryService.GetParticipantsAsync(parsedRoomId, context.CancellationToken);
        if (!result.IsSuccess)
            throw GrpcErrors.NotFound(TranslationRoomConstants.EntityTranslationRoom, request.RoomId);

        var response = new GetParticipantsByRoomIdResponse();

        // WT-263: the roster's IsActive is the same question the WT-262 capacity cap asks, so it
        // answers from the shared seat definition instead of its own status literal. Safe as a method
        // call because the directory service has already materialised the rows — this loop is
        // LINQ-to-Objects, not an EF predicate (the cap uses SeatHolding.Contains, which is what
        // translates to SQL). The null-coalescing the boundary used to apply now lives in the
        // projection that builds the summary, so these strings arrive non-null.
        foreach (var p in result.Value!)
        {
            response.Participants.Add(new Shared.Protos.Participant
            {
                Id = p.UserId?.ToString() ?? string.Empty,
                DisplayName = p.DisplayName,
                Role = p.Role,
                Language = p.SpeakLanguage,
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

        var result = await _directoryService.CountActiveRoomsByWorkspaceAsync(
            workspaceId,
            context.CancellationToken);

        return new GetActiveRoomCountByWorkspaceResponse { Count = result.Value };
    }
}
