using System;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Constants;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceMapperTests
{
    /// <summary>
    /// The length rule in CreateWorkspaceAsync measures the TRIMMED name, so the entity has to
    /// store the trimmed one. Storing the raw value let "140 characters plus 20 spaces" pass a
    /// guard reading 140 and then arrive at a varchar(150) column as 160 — a guard that measures
    /// one string and protects another is not a guard.
    /// </summary>
    [Fact]
    public void ToEntity_ShouldStoreTheTrimmedName_TheOneTheLengthRuleMeasured()
    {
        var padded = new string('a', WorkspaceConstants.WorkspaceNameMaxLength) + new string(' ', 20);
        var request = new CreateWorkspaceRequest(padded, null);

        var entity = request.ToEntity("acme", Guid.NewGuid());

        Assert.Equal(WorkspaceConstants.WorkspaceNameMaxLength, entity.Name.Length);
        Assert.True(
            entity.Name.Length <= WorkspaceConstants.WorkspaceNameMaxLength,
            $"name was {entity.Name.Length} characters, column holds {WorkspaceConstants.WorkspaceNameMaxLength}");
    }

    [Fact]
    public void ToEntity_ShouldNotAlterANameThatNeedsNoTrimming()
    {
        var entity = new CreateWorkspaceRequest("Acme Corp", null).ToEntity("acme-corp", Guid.NewGuid());

        Assert.Equal("Acme Corp", entity.Name);
    }
}
