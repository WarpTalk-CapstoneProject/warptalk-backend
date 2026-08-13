using System;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared.Protos;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Infrastructure.Services;

public class TranslationRoomGrpcService : ITranslationRoomGrpcService
{
    private readonly TranslationRoomService.TranslationRoomServiceClient _client;

    public TranslationRoomGrpcService(TranslationRoomService.TranslationRoomServiceClient client)
    {
        _client = client;
    }

    public async Task<Result<GetTranslationRoomResponse>> GetRoomDetailsAsync(Guid translationRoomId)
    {
        try
        {
            var response = await _client.GetTranslationRoomByIdAsync(new GetTranslationRoomRequest
            {
                Id = translationRoomId.ToString()
            });
            return Result.Success(response);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return Result.Failure<GetTranslationRoomResponse>("Room not found", "ROOM_NOT_FOUND");
        }
        catch (Exception ex)
        {
            return Result.Failure<GetTranslationRoomResponse>(ex.Message, "GRPC_ERROR");
        }
    }

    public async Task<Result<Guid>> TransferRoomHostAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid newHostUserId)
    {
        try
        {
            var response = await _client.TransferRoomHostAsync(new TransferRoomHostRequest
            {
                RoomId = translationRoomId.ToString(),
                NewHostUserId = newHostUserId.ToString(),
                RequestedByUserId = requestedByUserId.ToString()
            });

            // Empty means the room had never been transferred, so the booker was the outgoing host.
            return Guid.TryParse(response.PreviousHostUserId, out var previousHostId)
                ? Result.Success(previousHostId)
                : Result.Success(Guid.Empty);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return Result.Failure<Guid>("Room not found", "ROOM_NOT_FOUND");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            return Result.Failure<Guid>(ex.Status.Detail, "TRANSFER_FORBIDDEN");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            // The room service refused on its own roster — surfaced verbatim rather than reduced to
            // a generic gRPC error, because it is the actionable half ("not a participant there").
            return Result.Failure<Guid>(ex.Status.Detail, "TRANSFER_REFUSED");
        }
        catch (Exception ex)
        {
            return Result.Failure<Guid>(ex.Message, "GRPC_ERROR");
        }
    }

    public async Task<Result<GetParticipantsByRoomIdResponse>> GetParticipantsAsync(Guid translationRoomId)
    {
        try
        {
            var response = await _client.GetParticipantsByRoomIdAsync(new GetParticipantsByRoomIdRequest
            {
                RoomId = translationRoomId.ToString()
            });
            return Result.Success(response);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return Result.Failure<GetParticipantsByRoomIdResponse>("Participants not found", "PARTICIPANTS_NOT_FOUND");
        }
        catch (Exception ex)
        {
            return Result.Failure<GetParticipantsByRoomIdResponse>(ex.Message, "GRPC_ERROR");
        }
    }
}
