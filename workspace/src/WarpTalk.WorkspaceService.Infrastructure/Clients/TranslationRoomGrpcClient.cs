using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public class TranslationRoomGrpcClient : ITranslationRoomClient
{
    private readonly TranslationRoomService.TranslationRoomServiceClient _client;
    private readonly ILogger<TranslationRoomGrpcClient> _logger;

    public TranslationRoomGrpcClient(
        TranslationRoomService.TranslationRoomServiceClient client,
        ILogger<TranslationRoomGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<TranslationRoomDto?> GetTranslationRoomAsync(Guid roomId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = roomId.ToString() },
                cancellationToken: ct);

            return new TranslationRoomDto
            {
                Id = Guid.TryParse(response.Id, out var id) ? id : Guid.Empty,
                WorkspaceId = Guid.TryParse(response.WorkspaceId, out var wsId) ? wsId : Guid.Empty,
                Title = response.Title,
                HostId = Guid.TryParse(response.HostId, out var hostId) ? hostId : Guid.Empty,
                Status = response.Status,
                StartedAt = DateTime.TryParse(response.StartedAt, out var started) ? started : null,
                EndedAt = DateTime.TryParse(response.EndedAt, out var ended) ? ended : null
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC GetTranslationRoomById failed. RoomId: {RoomId}", roomId);
            return null;
        }
    }

    public async Task<TranslationRoomDto?> GetTranslationRoomByCodeAsync(string roomCode, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetTranslationRoomByCodeAsync(
                new GetTranslationRoomByCodeRequest { RoomCode = roomCode },
                cancellationToken: ct);

            return new TranslationRoomDto
            {
                Id = Guid.TryParse(response.Id, out var id) ? id : Guid.Empty,
                WorkspaceId = Guid.TryParse(response.WorkspaceId, out var wsId) ? wsId : Guid.Empty,
                Title = response.Title,
                HostId = Guid.TryParse(response.HostId, out var hostId) ? hostId : Guid.Empty,
                Status = response.Status,
                StartedAt = DateTime.TryParse(response.StartedAt, out var started) ? started : null,
                EndedAt = DateTime.TryParse(response.EndedAt, out var ended) ? ended : null
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC GetTranslationRoomByCode failed. RoomCode: {RoomCode}", roomCode);
            return null;
        }
    }


    public async Task<List<TranslationRoomParticipantDto>> GetParticipantsAsync(Guid roomId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetParticipantsByRoomIdAsync(
                new GetParticipantsByRoomIdRequest { RoomId = roomId.ToString() },
                cancellationToken: ct);

            var participants = new List<TranslationRoomParticipantDto>();
            foreach (var p in response.Participants)
            {
                participants.Add(new TranslationRoomParticipantDto
                {
                    Id = Guid.TryParse(p.Id, out var pId) ? pId : Guid.Empty,
                    DisplayName = p.DisplayName,
                    Role = p.Role,
                    Language = p.Language,
                    IsActive = p.IsActive
                });
            }
            return participants;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC GetParticipantsByRoomId failed. RoomId: {RoomId}", roomId);
            return new List<TranslationRoomParticipantDto>();
        }
    }
}
