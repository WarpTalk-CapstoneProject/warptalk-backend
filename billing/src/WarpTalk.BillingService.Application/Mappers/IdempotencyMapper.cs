using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Models;

namespace WarpTalk.BillingService.Application.Mappers;

public static class IdempotencyMapper
{
    public static IdempotencyRecord CreateRecord(IdempotencyKey key, string responseJson, Guid? workspaceId)
    {
        var now = DateTime.UtcNow;
        return new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            Key = key.Key,
            Operation = key.Operation,
            WorkspaceId = workspaceId,
            RequestHash = key.RequestHash,
            ResponseJson = responseJson,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7)
        };
    }
}
