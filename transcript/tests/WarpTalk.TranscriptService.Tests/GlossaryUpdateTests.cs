using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// Function 49 — Update Workspace Glossary: <see cref="GlossaryService.UpdateGlossaryAsync"/> plus
/// the data-annotation rules on <see cref="UpdateGlossaryDto"/> that the API enforces before the
/// service is ever reached.
/// </summary>
public class GlossaryUpdateTests
{
    private static readonly Guid GlossaryId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGlossaryRepository _glossaries = Substitute.For<IGlossaryRepository>();
    private readonly ILogger<GlossaryService> _logger = Substitute.For<ILogger<GlossaryService>>();
    private readonly GlossaryService _service;
    private readonly DateTime _originalUpdatedAt = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Glossary _existing;

    public GlossaryUpdateTests()
    {
        _unitOfWork.Glossaries.Returns(_glossaries);
        _unitOfWork.GlossaryTerms.Returns(Substitute.For<IGlossaryTermRepository>());

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase().ReturnsForAnyArgs(Substitute.For<IDatabase>());

        _existing = new Glossary
        {
            Id = GlossaryId,
            WorkspaceId = Guid.NewGuid(),
            Name = "Legal",
            Description = "Original description",
            SourceLanguage = "en",
            TargetLanguage = "vi",
            IsActive = true,
            UpdatedAt = _originalUpdatedAt,
        };

        _service = new GlossaryService(_unitOfWork, _logger, redis);
    }

    private void GlossaryExists() =>
        _glossaries.GetByIdAsync(GlossaryId, Arg.Any<CancellationToken>()).Returns(_existing);

    /// <summary>
    /// The rules sit on the record's positional parameters, so they are attached to the primary
    /// constructor's PARAMETERS, not to the generated properties. <c>Validator.TryValidateObject</c>
    /// only reads property attributes and therefore sees none of them; ASP.NET Core model
    /// validation reads the constructor parameters of a record. This helper does what MVC does:
    /// each parameter's attributes are checked against the matching property value.
    /// </summary>
    private static IList<ValidationResult> Validate(UpdateGlossaryDto dto)
    {
        var results = new List<ValidationResult>();
        var constructor = typeof(UpdateGlossaryDto).GetConstructors()
            .Single(c => c.GetParameters().Length == 3);

        foreach (var parameter in constructor.GetParameters())
        {
            var property = typeof(UpdateGlossaryDto).GetProperty(parameter.Name!)!;
            var attributes = parameter.GetCustomAttributes(typeof(ValidationAttribute), inherit: true)
                .Cast<ValidationAttribute>();
            var context = new ValidationContext(dto) { MemberName = property.Name };
            Validator.TryValidateValue(property.GetValue(dto), context, results, attributes);
        }

        return results;
    }

    // UTCID01
    [Fact]
    public async Task Update_ExistingGlossary_WithNewValues_UpdatesFieldsAndSaves()
    {
        GlossaryExists();
        var dto = new UpdateGlossaryDto("Legal v2", "Updated description", true);

        var result = await _service.UpdateGlossaryAsync(GlossaryId, dto);

        Assert.True(result.IsSuccess);
        Assert.Empty(Validate(dto));
        Assert.Equal("Legal v2", _existing.Name);
        Assert.Equal("Updated description", _existing.Description);
        Assert.True(_existing.IsActive);
        Assert.True(_existing.UpdatedAt > _originalUpdatedAt);
        _glossaries.Received(1).Update(_existing);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID02
    [Fact]
    public async Task Update_ExistingGlossary_WithNullDescription_Succeeds()
    {
        GlossaryExists();
        var dto = new UpdateGlossaryDto("Legal v2", null, true);

        var result = await _service.UpdateGlossaryAsync(GlossaryId, dto);

        Assert.True(result.IsSuccess);
        Assert.Empty(Validate(dto));
        Assert.Null(_existing.Description);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID03
    [Fact]
    public async Task Update_ExistingGlossary_Deactivating_Succeeds()
    {
        GlossaryExists();
        var dto = new UpdateGlossaryDto("Legal v2", "Updated description", false);

        var result = await _service.UpdateGlossaryAsync(GlossaryId, dto);

        Assert.True(result.IsSuccess);
        Assert.False(_existing.IsActive);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID04
    [Fact]
    public async Task Update_MissingGlossary_ReturnsNotFound_AndSavesNothing()
    {
        _glossaries.GetByIdAsync(GlossaryId, Arg.Any<CancellationToken>()).Returns((Glossary?)null);

        var result = await _service.UpdateGlossaryAsync(GlossaryId, new UpdateGlossaryDto("Legal v2", null, true));

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        _glossaries.DidNotReceiveWithAnyArgs().Update(default!);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID05
    [Fact]
    public void UpdateDto_WithNullName_FailsValidation()
    {
        var results = Validate(new UpdateGlossaryDto(null!, "Updated description", true));

        var error = Assert.Single(results);
        Assert.Contains(nameof(UpdateGlossaryDto.Name), error.MemberNames);
        Assert.Equal("Name is required.", error.ErrorMessage);
    }

    // UTCID06
    [Fact]
    public void UpdateDto_WithNameOf101Characters_FailsValidation()
    {
        Assert.Empty(Validate(new UpdateGlossaryDto(new string('a', 100), null, true)));

        var results = Validate(new UpdateGlossaryDto(new string('a', 101), null, true));

        var error = Assert.Single(results);
        Assert.Contains(nameof(UpdateGlossaryDto.Name), error.MemberNames);
        Assert.Equal("Name cannot exceed 100 characters.", error.ErrorMessage);
    }

    // UTCID07
    [Fact]
    public void UpdateDto_WithDescriptionOf501Characters_FailsValidation()
    {
        Assert.Empty(Validate(new UpdateGlossaryDto("Legal v2", new string('d', 500), true)));

        var results = Validate(new UpdateGlossaryDto("Legal v2", new string('d', 501), true));

        var error = Assert.Single(results);
        Assert.Contains(nameof(UpdateGlossaryDto.Description), error.MemberNames);
        Assert.Equal("Description cannot exceed 500 characters.", error.ErrorMessage);
    }

    // UTCID08
    [Fact]
    public async Task Update_WhenSaveChangesThrows_ReturnsInternalError_AndLogsError()
    {
        GlossaryExists();
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var result = await _service.UpdateGlossaryAsync(GlossaryId, new UpdateGlossaryDto("Legal v2", null, true));

        Assert.False(result.IsSuccess);
        Assert.Equal("INTERNAL_ERROR", result.ErrorCode);
        Assert.Contains(_logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log)
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Error
            && call.GetArguments()[3] is InvalidOperationException);
    }
}
