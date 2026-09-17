using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.API.Controllers;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.API.Controllers;

/// <summary>
/// TranslationRoomsController.InviteParticipants — request-shape guard in front of
/// TranslationRoomService.InviteParticipantsAsync (service behaviour: InviteDuringMeetingTests).
/// </summary>
public class TranslationRoomsControllerInviteParticipantsTests
{
    private readonly Mock<ITranslationRoomService> _mockRoomService = new();
    private readonly TranslationRoomsController _controller;

    private static readonly Guid UserId = Guid.NewGuid();

    public TranslationRoomsControllerInviteParticipantsTests()
    {
        _controller = new TranslationRoomsController(
            _mockRoomService.Object,
            Mock.Of<ITranslationRoomArtifactService>(),
            Mock.Of<ITranslationRoomSeriesService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) }, "Test")),
                },
            },
        };
    }

    public static TheoryData<InviteParticipantsRequest?> EmptyEmailRequests => new()
    {
        new InviteParticipantsRequest(new List<string>()),
        new InviteParticipantsRequest(null!),
        null,
    };

    [Theory] // UTCID07
    [MemberData(nameof(EmptyEmailRequests))]
    public async Task InviteParticipants_ShouldReturnBadRequest_WhenNoEmailsProvided(InviteParticipantsRequest? request)
    {
        var result = await _controller.InviteParticipants(Guid.NewGuid(), request!, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = badRequest.Value.Should().BeOfType<ApiErrorResponse>().Subject;
        body.Error.Should().Be("At least one email is required.");
        body.Code.Should().Be(ErrorCodes.ValidationError);
        _mockRoomService.Verify(s => s.InviteParticipantsAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
