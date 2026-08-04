using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Mappers;

namespace WarpTalk.TranscriptService.Tests;

public sealed class TranscriptCorrectionMapperTests
{
    [Fact]
    public void ToEntity_UsesAuthenticatedUserInsteadOfClientSuppliedIdentity()
    {
        var authenticatedUserId = Guid.NewGuid();
        var spoofedUserId = Guid.NewGuid();
        var dto = new CreateCorrectionDto(
            spoofedUserId,
            "before",
            "after",
            "STT");

        var entity = dto.ToEntity(Guid.NewGuid(), authenticatedUserId);

        Assert.Equal(authenticatedUserId, entity.UserId);
        Assert.NotEqual(spoofedUserId, entity.UserId);
    }
}
