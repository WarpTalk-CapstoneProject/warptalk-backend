using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WarpTalk.Shared;
using WarpTalk.Shared.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// Function 25 - Request To Leave Workspace (WorkspaceInvitationService.CreateLeaveRequestAsync).
/// UTCID01 and UTCID04 are covered by WorkspaceLeaveRequestServiceTests; this class covers the rest.
/// </summary>
public class WorkspaceCreateLeaveRequestServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IWorkspaceInvitationRepository _workspaceInvitationRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly CapturingLogger _logger = new();
    private readonly WorkspaceInvitationService _service;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private const string UserEmail = "member@example.com";

    public WorkspaceCreateLeaveRequestServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _workspaceInvitationRepository = Substitute.For<IWorkspaceInvitationRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.WorkspaceInvitationRepository.Returns(_workspaceInvitationRepository);

        _service = new WorkspaceInvitationService(
            _unitOfWork,
            _logger,
            _authIdentity,
            Substitute.For<ITranslationRoomClient>(),
            Substitute.For<IWorkspaceInvitationEmailComposer>(),
            Substitute.For<IBillingSubscriptionClient>(),
            Substitute.For<IWorkspaceInvitationAcceptanceProcessor>());
    }

    private WorkspaceMember ArrangeActiveMember(string roleName)
    {
        var roleId = Guid.NewGuid();
        var workspace = new Workspace { Id = _workspaceId, Name = "Acme", IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = _workspaceId, UserId = _userId, RoleId = roleId, MembershipType = "Internal" };

        _workspaceRepository.GetByIdAsync(_workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = roleId, Name = roleName });
        _authIdentity.GetRoleByNameAsync(roleName, Arg.Any<CancellationToken>()).Returns(new Role { Id = roleId, Name = roleName });
        return member;
    }

    [Fact]
    public async Task CreateLeaveRequest_UTCID02_UnknownWorkspace_ReturnsNotFound()
    {
        _workspaceRepository.GetByIdAsync(_workspaceId, Arg.Any<CancellationToken>()).Returns((Workspace?)null);

        var result = await _service.CreateLeaveRequestAsync(_workspaceId, _userId, UserEmail);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.WorkspaceNotFound, result.Error);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _workspaceInvitationRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceInvitation>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateLeaveRequest_UTCID03_NonMember_ReturnsForbidden()
    {
        _workspaceRepository.GetByIdAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = _workspaceId, Name = "Acme", IsActive = true });
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);

        var result = await _service.CreateLeaveRequestAsync(_workspaceId, _userId, UserEmail);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.UserNotMember, result.Error);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _workspaceInvitationRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceInvitation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateLeaveRequest_UTCID05_PendingLeaveRequestExists_ReturnsExistingWithoutDuplicate()
    {
        ArrangeActiveMember("Member");
        var existing = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            Email = UserEmail,
            RequestedBy = _userId,
            MembershipType = "Internal",
            Status = InvitationStatus.LEAVE_REQUESTED.ToString()
        };
        _workspaceInvitationRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceInvitation, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _service.CreateLeaveRequestAsync(_workspaceId, _userId, UserEmail);

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value!.Id);
        Assert.Equal(InvitationStatus.LEAVE_REQUESTED.ToString(), result.Value.Status);
        await _workspaceInvitationRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceInvitation>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateLeaveRequest_UTCID06_OwnerWithAnotherActiveOwner_Succeeds()
    {
        var member = ArrangeActiveMember("Owner");
        _workspaceMemberRepository.CountActiveOwnersAsync(_workspaceId, member.RoleId, Arg.Any<CancellationToken>()).Returns(2);

        var result = await _service.CreateLeaveRequestAsync(_workspaceId, _userId, UserEmail);

        Assert.True(result.IsSuccess);
        Assert.Equal(_workspaceId, result.Value!.WorkspaceId);
        Assert.Equal(InvitationStatus.LEAVE_REQUESTED.ToString(), result.Value.Status);
        await _workspaceInvitationRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceInvitation>(i => i.Status == "LEAVE_REQUESTED" && i.RequestedBy == _userId),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateLeaveRequest_UTCID07_SaveThrows_ReturnsFailureAndLogsError()
    {
        ArrangeActiveMember("Member");
        var boom = new InvalidOperationException("db down");
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(boom);

        var result = await _service.CreateLeaveRequestAsync(_workspaceId, _userId, UserEmail);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.UnexpectedError, result.Error);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);

        var entry = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Same(boom, entry.Exception);
        Assert.Equal(
            $"Error occurred while creating leave request for workspace {_workspaceId}, user {_userId}",
            entry.Message);
    }

    [Fact]
    public async Task CreateLeaveRequest_UTCID07_AddThrows_ReturnsFailureAndLogsError()
    {
        ArrangeActiveMember("Member");
        _workspaceInvitationRepository.AddAsync(Arg.Any<WorkspaceInvitation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("insert failed"));

        var result = await _service.CreateLeaveRequestAsync(_workspaceId, _userId, UserEmail);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error
            && e.Message == $"Error occurred while creating leave request for workspace {_workspaceId}, user {_userId}");
    }

    private sealed class CapturingLogger : ILogger<WorkspaceInvitationService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
