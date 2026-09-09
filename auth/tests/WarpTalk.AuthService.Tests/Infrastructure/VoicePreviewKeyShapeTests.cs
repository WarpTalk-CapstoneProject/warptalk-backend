using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.AuthService.Infrastructure.Clients;

namespace WarpTalk.AuthService.Tests.Infrastructure;

/// <summary>
/// WT-632 — the Redis key a preview is waited for under must be the one Python writes.
///
/// WHY THIS IS A TEST AND NOT A COMMENT
///     Every existing preview test mocks <c>IVoicePreviewQueue</c> and passes a bare "vi", so
///     the whole class of failure below sat underneath all of them. The AI worker keys its
///     answer by `base_language(language)`; the "Your voices" rows on the voice-profiles page
///     send the profile's STORED language, which is a locale tag ("vi-VN"). The render happened,
///     was cached, and was then waited for at a key nothing would ever write — twelve seconds of
///     spinner, "the preview is taking longer than expected", every press, forever, with a fresh
///     paid synthesis behind each one because the cache read missed too.
///
///     The only thing that catches that is asserting on the key itself.
/// </summary>
public sealed class VoicePreviewKeyShapeTests
{
    private const string VoiceId = "11111111-2222-3333-4444-555555555555";

    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly RedisVoicePreviewQueue _queue;

    public VoicePreviewKeyShapeTests()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_database);
        _queue = new RedisVoicePreviewQueue(
            redis, Substitute.For<ILogger<RedisVoicePreviewQueue>>());
    }

    private void Rendered(string key, byte[] audio) =>
        _database.StringGetAsync(key, Arg.Any<CommandFlags>()).Returns(
            (RedisValue)JsonSerializer.Serialize(
                new { audio = Convert.ToBase64String(audio), error = (string?)null }));

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("vi")]
    [InlineData("VI-vn")]
    [InlineData("vi_VN")]
    [InlineData("  vi-VN  ")]
    public async Task A_render_is_found_whatever_shape_the_language_arrives_in(string language)
    {
        // What the AI worker actually wrote. Not a choice this side gets to make.
        var wav = Encoding.ASCII.GetBytes("RIFFpretend-audio");
        Rendered($"voice:preview:{VoiceId}:vi", wav);

        var found = await _queue.TryGetAsync(VoiceId, language);

        Assert.NotNull(found);
        Assert.Equal(wav, found!.Audio);
    }

    [Fact]
    public async Task A_locale_tag_is_not_looked_up_under_a_key_nothing_writes()
    {
        // The regression itself, stated as the negative: "vi-VN" must never reach Redis.
        await _queue.TryGetAsync(VoiceId, "vi-VN");

        await _database.DidNotReceive().StringGetAsync(
            $"voice:preview:{VoiceId}:vi-VN", Arg.Any<CommandFlags>());
        await _database.Received().StringGetAsync(
            $"voice:preview:{VoiceId}:vi", Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task The_queued_request_names_the_same_language_the_answer_is_keyed_by()
    {
        // Otherwise the two halves could drift apart again from the other end: a request asking
        // for "vi-VN" and a wait on "vi" is the same bug pointing the other way.
        Assert.True(await _queue.RequestAsync(VoiceId, "vi-VN"));

        // Read off the recorded call rather than matched against the signature: StreamAddAsync
        // has several overloads and gains optional parameters between StackExchange.Redis
        // releases, and this test is about the payload, not about that shape.
        var call = _database.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamAddAsync));
        var arguments = call.GetArguments();

        Assert.Equal("voice:preview_requests", ((RedisKey)arguments[0]!).ToString());
        var entries = Assert.IsType<NameValueEntry[]>(arguments[1]);
        Assert.Contains(entries, entry => entry.Name == "voice_id" && entry.Value == VoiceId);
        Assert.Contains(entries, entry => entry.Name == "language" && entry.Value == "vi");
    }
}
