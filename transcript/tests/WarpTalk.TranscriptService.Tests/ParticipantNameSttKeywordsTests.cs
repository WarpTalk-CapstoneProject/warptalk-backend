using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// WT-422. The people in a meeting are the proper nouns STT gets wrong most reliably — "Huỳnh Thái
/// Tú", "Tanaka Haruto" — and no glossary holds them. Their names now ride in
/// <c>translationRoom:{room}:stt_keywords</c> after the glossary terms, in a budget of their own.
/// </summary>
public sealed class ParticipantNameSttKeywordsTests
{
    private static readonly string[] Glossary = ["WarpTalk", "Codex", "kiến trúc"];

    [Fact]
    public void Names_follow_the_glossary_keywords_which_are_left_untouched()
    {
        var keywords = GlossaryStartedEventConsumer.AppendParticipantNames(
            Glossary, ["Huỳnh Thái Tú", "Tanaka Haruto"], maxNames: 6);

        Assert.Equal(["WarpTalk", "Codex", "kiến trúc", "Huỳnh Thái Tú", "Tanaka Haruto"], keywords);
    }

    [Fact]
    public void A_name_is_trimmed_and_its_whitespace_collapsed_like_the_reader_does()
    {
        var keywords = GlossaryStartedEventConsumer.AppendParticipantNames(
            [], ["  Ngô   Xuân\tHạnh Nhi  "], maxNames: 6);

        Assert.Equal(["Ngô Xuân Hạnh Nhi"], keywords);
    }

    [Fact]
    public void A_name_already_in_the_glossary_or_already_added_does_not_take_a_second_slot()
    {
        // Decomposed (NFD) Vietnamese is what a macOS keyboard can hand over; it is the same name.
        var decomposed = "Huỳnh Thái Tú".Normalize(NormalizationForm.FormD);
        Assert.NotEqual("Huỳnh Thái Tú", decomposed);

        var keywords = GlossaryStartedEventConsumer.AppendParticipantNames(
            Glossary,
            ["warptalk", "Huỳnh Thái Tú", "HUỲNH  THÁI TÚ", decomposed, "Tanaka Haruto"],
            maxNames: 6);

        Assert.Equal(["WarpTalk", "Codex", "kiến trúc", "Huỳnh Thái Tú", "Tanaka Haruto"], keywords);
        Assert.All(keywords, k => Assert.True(k.IsNormalized(NormalizationForm.FormC)));
    }

    [Fact]
    public void Names_that_are_unsafe_or_unusable_as_keywords_are_skipped()
    {
        var keywords = GlossaryStartedEventConsumer.AppendParticipantNames(
            [],
            [
                null,
                "   ",
                // Two letters, one capital: also an ordinary Vietnamese syllable. Biasing toward it
                // would pull ordinary speech into the name — the WT-426 failure.
                "Tú",
                // Not something anyone says.
                "tu.huynh@example.com",
                // Longer than the reader keeps whole; cut, it would be a mangled name.
                new string('A', GlossaryStartedEventConsumer.MaxSttKeywordLength + 1),
                "Lê Văn",
            ],
            maxNames: 6);

        Assert.Equal(["Lê Văn"], keywords);
    }

    [Fact]
    public void At_most_the_name_budget_is_added_and_a_skipped_name_frees_its_slot_for_the_next()
    {
        var names = new[] { "Tú", "Person One", "Person Two", "Person Three", "Person Four" };

        var keywords = GlossaryStartedEventConsumer.AppendParticipantNames(Glossary, names, maxNames: 2);

        Assert.Equal(["WarpTalk", "Codex", "kiến trúc", "Person One", "Person Two"], keywords);
    }

    [Fact]
    public void A_zero_budget_adds_nothing()
    {
        var keywords = GlossaryStartedEventConsumer.AppendParticipantNames(Glossary, ["Tanaka Haruto"], maxNames: 0);

        Assert.Equal(Glossary, keywords);
    }

    /// <summary>
    /// warptalk-ai reads at most 16 keywords (stt_worker/worker.py _MAX_STT_KEYWORDS) and drops the
    /// rest from the END, silently. A full glossary plus a full set of names must fit, or the names
    /// are the ones that vanish.
    /// </summary>
    [Fact]
    public void A_full_glossary_and_a_full_set_of_names_fit_inside_what_the_STT_worker_reads()
    {
        Assert.True(
            GlossaryStartedEventConsumer.MaxSttKeywords + GlossaryStartedEventConsumer.MaxParticipantNameSttKeywords
                <= GlossaryStartedEventConsumer.SttWorkerKeywordCeiling);
    }

    [Fact]
    public async Task Publish_writes_glossary_then_names_as_the_json_string_array_the_STT_worker_parses()
    {
        var roomClient = new FakeRoomClient("Huỳnh Thái Tú", "Tanaka Haruto", "warptalk", "Tú");
        var (consumer, database) = Build(roomClient, glossaryTerms: [("WarpTalk", "WarpTalk")]);
        var roomId = Guid.NewGuid().ToString();

        await consumer.PublishGlossaryPromptsAsync(roomId, WorkspaceId, "Sprint review", null, CancellationToken.None);

        var keywords = JsonSerializer.Deserialize<List<string>>(Written(database, $"translationRoom:{roomId}:stt_keywords")!);
        Assert.Equal(["WarpTalk", "Huỳnh Thái Tú", "Tanaka Haruto"], keywords);

        Assert.Equal(roomId, roomClient.LastRequest!.RoomId);
        Assert.Equal(GlossaryStartedEventConsumer.MaxParticipantNameSttKeywords * 2, roomClient.LastRequest.MaxNames);
    }

    [Fact]
    public async Task Publish_keeps_the_glossary_keywords_when_the_roster_cannot_be_reached()
    {
        var roomClient = new FakeRoomClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "down")) };
        var (consumer, database) = Build(roomClient, glossaryTerms: [("WarpTalk", "WarpTalk")]);
        var roomId = Guid.NewGuid().ToString();

        await consumer.PublishGlossaryPromptsAsync(roomId, WorkspaceId, "Sprint review", null, CancellationToken.None);

        var keywords = JsonSerializer.Deserialize<List<string>>(Written(database, $"translationRoom:{roomId}:stt_keywords")!);
        Assert.Equal(["WarpTalk"], keywords);
    }

    /// <summary>
    /// A room with no glossary and no title or description used to publish nothing at all. Its
    /// people's names are still worth hearing correctly — and there is no prose prompt to write.
    /// </summary>
    [Fact]
    public async Task Publish_still_writes_the_names_for_a_room_with_no_glossary_and_no_context()
    {
        var roomClient = new FakeRoomClient("Huỳnh Thái Tú");
        var (consumer, database) = Build(roomClient, glossaryTerms: []);
        var roomId = Guid.NewGuid().ToString();

        await consumer.PublishGlossaryPromptsAsync(roomId, WorkspaceId, null, null, CancellationToken.None);

        var keywords = JsonSerializer.Deserialize<List<string>>(Written(database, $"translationRoom:{roomId}:stt_keywords")!);
        Assert.Equal(["Huỳnh Thái Tú"], keywords);
        Assert.Null(Written(database, $"translationRoom:{roomId}:stt_prompt"));
    }

    private static readonly Guid WorkspaceId = Guid.NewGuid();

    private static (GlossaryStartedEventConsumer Consumer, IDatabase Database) Build(
        FakeRoomClient roomClient,
        (string Source, string Target)[] glossaryTerms)
    {
        var glossary = new Glossary { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, Name = "Main", IsActive = true };
        var terms = glossaryTerms
            .Select(t => new GlossaryTerm
            {
                Id = Guid.NewGuid(),
                GlossaryId = glossary.Id,
                SourceTerm = t.Source,
                TargetTerm = t.Target,
                Priority = 1,
                IsActive = true,
            })
            .ToList();

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.Glossaries
            .FindAsync(Arg.Any<Expression<Func<Glossary, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { glossary }.Where(call.Arg<Expression<Func<Glossary, bool>>>().Compile()));
        unitOfWork.GlossaryTerms
            .FindAsync(Arg.Any<Expression<Func<GlossaryTerm, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => terms.Where(call.Arg<Expression<Func<GlossaryTerm, bool>>>().Compile()));

        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        var services = new ServiceCollection()
            .AddSingleton(unitOfWork)
            .AddSingleton<WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>(roomClient)
            .BuildServiceProvider();

        var consumer = new GlossaryStartedEventConsumer(
            redis,
            services,
            // The platform glossary is a separate path with its own tests; keep it out of the way.
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["GlobalGlossary:Enabled"] = "false" })
                .Build(),
            NullLogger<GlossaryStartedEventConsumer>.Instance);

        return (consumer, database);
    }

    /// <summary>The value last written to <paramref name="key"/>, or null if it was never written.</summary>
    private static string? Written(IDatabase database, string key) =>
        database.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync))
            .Select(c => c.GetArguments())
            .Where(args => args[0] is RedisKey k && k.ToString() == key)
            .Select(args => ((RedisValue)args[1]!).ToString())
            .LastOrDefault();

    private sealed class FakeRoomClient : WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient
    {
        private readonly string[] _names;

        public FakeRoomClient(params string[] names) => _names = names;

        public RpcException? Failure { get; init; }
        public GetRoomPeopleNamesRequest? LastRequest { get; private set; }

        public override AsyncUnaryCall<GetRoomPeopleNamesResponse> GetRoomPeopleNamesAsync(
            GetRoomPeopleNamesRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Failure is not null)
                throw Failure;

            var response = new GetRoomPeopleNamesResponse();
            response.DisplayNames.AddRange(_names);
            return new AsyncUnaryCall<GetRoomPeopleNamesResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }
}
