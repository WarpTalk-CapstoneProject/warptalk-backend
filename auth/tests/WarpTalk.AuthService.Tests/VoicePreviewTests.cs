using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// Hearing a voice before a meeting instead of during one.
///
/// Two things are worth pinning beyond "audio comes back". The first is the AUTHORIZATION rule:
/// a voice cloned from somebody's recording is theirs, so an arbitrary provider id must not be
/// renderable — otherwise the play button is a way to sample another person's voice. The second
/// is that the CACHE is consulted before the queue, because that is what makes every play after
/// the first cost no synthesis.
/// </summary>
public class VoicePreviewTests
{
    private const string CatalogVoiceId = "935a9060-373c-49e4-b078-f4ea6326987a";
    private const string OwnCloneVoiceId = "11111111-2222-3333-4444-555555555555";
    private const string SomebodyElsesVoiceId = "99999999-8888-7777-6666-555555555555";

    private static readonly byte[] Wav = Encoding.ASCII.GetBytes("RIFFpretend-audio");

    private readonly Guid _userId = Guid.NewGuid();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IVoiceProfileRepository _profiles = Substitute.For<IVoiceProfileRepository>();
    private readonly IVoiceCatalogDirectory _catalog = Substitute.For<IVoiceCatalogDirectory>();
    private readonly IVoicePreviewQueue _previews = Substitute.For<IVoicePreviewQueue>();
    private readonly VoiceProfileService _service;

    public VoicePreviewTests()
    {
        _unitOfWork.VoiceProfileRepository.Returns(_profiles);
        _profiles.GetByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<VoiceProfile>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    UserId = _userId,
                    Language = "vi",
                    EmbeddingRef = OwnCloneVoiceId,
                    IsActive = true,
                },
            });
        _catalog.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<VoiceCatalogItemDto>
            {
                new(CatalogVoiceId, "Linh - Soft Presence", "feminine"),
            });

        _service = new VoiceProfileService(
            _unitOfWork,
            Substitute.For<IVoiceSampleStorage>(),
            _catalog,
            Substitute.For<IVoiceCloneRequestQueue>(),
            _previews,
            Substitute.For<ILogger<VoiceProfileService>>());
    }

    private Task<WarpTalk.Shared.Result<byte[]>> Preview(string? voiceId, string? language = "vi") =>
        _service.PreviewVoiceAsync(_userId, new PreviewVoiceRequest(voiceId, language));

    [Fact]
    public async Task A_cached_render_is_returned_without_touching_the_queue()
    {
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns(new VoicePreview(Wav, null));

        var result = await Preview(CatalogVoiceId);

        Assert.True(result.IsSuccess);
        Assert.Equal(Wav, result.Value);
        await _previews.DidNotReceive().RequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_cold_voice_is_queued_and_waited_for()
    {
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns((VoicePreview?)null);
        _previews.RequestAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>()).Returns(true);
        _previews.WaitAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns(new VoicePreview(Wav, null));

        var result = await Preview(CatalogVoiceId);

        Assert.True(result.IsSuccess);
        Assert.Equal(Wav, result.Value);
    }

    [Fact]
    public async Task A_users_own_cloned_voice_is_previewable()
    {
        _previews.TryGetAsync(OwnCloneVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns(new VoicePreview(Wav, null));

        Assert.True((await Preview(OwnCloneVoiceId)).IsSuccess);
    }

    [Fact]
    public async Task Somebody_elses_voice_id_is_refused_before_anything_is_rendered()
    {
        // The access-control case. Without this the play button renders audio from any provider
        // id somebody can name, including a voice cloned from another person's recording.
        var result = await Preview(SomebodyElsesVoiceId);

        Assert.False(result.IsSuccess);
        await _previews.DidNotReceive().RequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _previews.DidNotReceive().TryGetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_language_is_refused_rather_than_defaulted()
    {
        // The sample is SPOKEN in the language, so guessing one would render a voice saying a
        // sentence in a language nobody asked to judge it in.
        var result = await Preview(CatalogVoiceId, language: null);

        Assert.False(result.IsSuccess);
        Assert.Contains("language", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_voice_is_refused()
    {
        Assert.False((await Preview("  ")).IsSuccess);
    }

    [Fact]
    public async Task A_queue_that_cannot_accept_the_request_says_so_instead_of_waiting()
    {
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns((VoicePreview?)null);
        _previews.RequestAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>()).Returns(false);

        var result = await Preview(CatalogVoiceId);

        Assert.False(result.IsSuccess);
        // The queue could not be reached, which says nothing about the voice.
        Assert.Equal(ErrorCodes.ServiceUnavailable, result.ErrorCode);
        // Nobody asked for this render, so no answer is coming — waiting the timeout out would
        // only make an immediate "unavailable" look like a slow success.
        await _previews.DidNotReceive().WaitAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_render_that_does_not_arrive_in_time_is_a_named_outcome()
    {
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns((VoicePreview?)null);
        _previews.RequestAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>()).Returns(true);
        _previews.WaitAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns((VoicePreview?)null);

        var result = await Preview(CatalogVoiceId);

        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        // WT-649: this used to be InvalidState, which contradicted the message sitting next to it
        // and read as "there is something wrong with this voice". Nothing on this path looks at the
        // profile's state at all — the render simply did not arrive inside RenderTimeout.
        Assert.Equal(ErrorCodes.ServiceUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task A_rendered_failure_is_reported_and_never_played_as_empty_audio()
    {
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns(new VoicePreview(null, "cartesia refused this voice", "VOICE_NOT_RENDERABLE"));

        var result = await Preview(CatalogVoiceId);

        Assert.False(result.IsSuccess);
        Assert.Equal("This voice cannot be previewed.", result.Error);
    }

    /// <summary>
    /// This test used to assert the opposite — that the provider's own wording was handed
    /// straight to the caller. That is how a Cartesia SDK exception reached a play button:
    ///
    ///     "Error code: 404 - {'error_code': 'voice_not_found', ..., 'request_id': 'e9d42fe9-…'}"
    ///
    /// A request id and a Python dict, shown to somebody who pressed play. The provider's text is
    /// for the log; the sentence comes from the code.
    /// </summary>
    [Fact]
    public async Task The_providers_own_wording_never_reaches_the_caller()
    {
        const string providerText =
            "Error code: 404 - {'error_code': 'voice_not_found', 'request_id': 'e9d42fe9'}";
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns(new VoicePreview(null, providerText, "VOICE_NOT_FOUND"));

        var result = await Preview(CatalogVoiceId);

        Assert.False(result.IsSuccess);
        Assert.Equal("This voice is no longer available from the provider.", result.Error);
        Assert.DoesNotContain("request_id", result.Error!);
        Assert.DoesNotContain("Error code", result.Error!);
    }

    [Fact]
    public async Task An_unrecognised_code_falls_back_to_the_generic_line_not_the_providers_text()
    {
        // The case that matters for deploy order and for any future worker: a code this build has
        // never seen must NOT cause the provider's wording to be shown instead.
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns(new VoicePreview(null, "Traceback (most recent call last): …", "SOMETHING_NEW"));

        var result = await Preview(CatalogVoiceId);

        Assert.False(result.IsSuccess);
        Assert.Equal("The preview could not be rendered.", result.Error);
        Assert.DoesNotContain("Traceback", result.Error!);
    }

    [Fact]
    public async Task A_missing_code_also_falls_back_rather_than_leaking()
    {
        // An older worker sends no code at all. Same rule.
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns(new VoicePreview(null, "cartesia: 500 internal", null));

        var result = await Preview(CatalogVoiceId);

        Assert.False(result.IsSuccess);
        Assert.Equal("The preview could not be rendered.", result.Error);
    }

    /// <summary>
    /// The code has to follow the cause, not just the sentence. Answering a provider outage with
    /// InvalidState is WT-649's original complaint reproduced one branch over: it tells a client
    /// keying off the code that something is wrong with the VOICE.
    /// </summary>
    [Theory]
    [InlineData("VOICE_NOT_FOUND", ErrorCodes.InvalidState)]
    [InlineData("VOICE_NOT_RENDERABLE", ErrorCodes.InvalidState)]
    [InlineData("NO_AUDIO", ErrorCodes.InvalidState)]
    [InlineData("PROVIDER_BUSY", ErrorCodes.ServiceUnavailable)]
    [InlineData("PROVIDER_UNREACHABLE", ErrorCodes.ServiceUnavailable)]
    [InlineData("PROVIDER_UNAVAILABLE", ErrorCodes.ServiceUnavailable)]
    [InlineData("PROVIDER_REJECTED", ErrorCodes.ServiceUnavailable)]
    [InlineData("SOMETHING_NEW", ErrorCodes.InvalidState)]
    public async Task The_error_code_names_whose_fault_it_was(string workerCode, string expectedCode)
    {
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns(new VoicePreview(null, "provider text nobody should see", workerCode));

        var result = await Preview(CatalogVoiceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.DoesNotContain("provider text", result.Error!);
    }

    [Fact]
    public async Task An_empty_render_is_a_failure_rather_than_a_successful_silence()
    {
        _previews.TryGetAsync(CatalogVoiceId, "vi", Arg.Any<CancellationToken>())
            .Returns(new VoicePreview(Array.Empty<byte>(), null));

        Assert.False((await Preview(CatalogVoiceId)).IsSuccess);
    }
}
