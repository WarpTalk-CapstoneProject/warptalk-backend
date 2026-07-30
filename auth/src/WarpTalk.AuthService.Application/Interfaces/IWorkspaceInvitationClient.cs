using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IWorkspaceInvitationClient
{
    Task<VerifyInvitationResult> VerifyInvitationTokenAsync(string token, CancellationToken ct = default);
    Task<AcceptInvitationResult> AcceptInvitationAsync(string token, Guid userId, string email, CancellationToken ct = default);
}
