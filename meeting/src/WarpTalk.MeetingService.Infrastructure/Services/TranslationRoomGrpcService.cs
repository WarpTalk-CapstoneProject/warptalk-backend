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
