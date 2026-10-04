using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-933. The host records which Meet-side people agreed to voice cloning.
///
/// Two things are pinned here. The first is the digest: the TTS worker in warptalk-ai computes
/// the same field from the name on a caption and looks it up in the hash this service writes, and
/// nothing but the two functions agreeing byte for byte connects them. The second is who may
/// write it and what is kept — the host only, in a bridge room that is still open, and never the
/// name itself.
/// </summary>
public class BridgeVoiceCloneConsentTests
{
    private static readonly Guid RoomId = Guid.Parse("3f2b6c1e-9a4d-4e7b-8c21-5d0a7e9f1b34");
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid GuestId = Guid.NewGuid();

    private const string StandIn = "00000000-0000-0000-0000-00000000b21d";
    private const string Name = "Trần  An";

    private static string Key => $"translationRoom:{RoomId}:far_speaker_clone_consents";

    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly HashRedis _redis = new();
    private readonly RecordingLogger _logger = new();
    private readonly BridgeVoiceCloneConsentService _service;

    public BridgeVoiceCloneConsentTests()
    {
        _service = new BridgeVoiceCloneConsentService(_rooms.Object, _redis, _logger);
    }

    private void RoomIs(
        string type = TranslationRoomTypes.ExternalBridge,
        string status = "IN_PROGRESS",
        Guid? activeHostId = null) =>
        _rooms.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom
            {
                Id = RoomId,
                HostId = HostId,
                ActiveHostId = activeHostId,
                TranslationRoomType = type,
                Status = status,
            });

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // ── fold and the consent field: the contract with tts_worker/far_speaker_clone.py ─────────

    [Theory]
    [InlineData("Trần  An", "trần an")]
    [InlineData("  TÚ Huỳnh ", "tú huỳnh")]
    [InlineData("Ａｎ Ｎｇｕｙｅｎ", "an nguyen")] // fullwidth letters: NFKC, not NFC
    [InlineData("An\t\n Nguyen", "an nguyen")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void FoldIsNfkcThenLowerThenSingleSpaces(string? name, string expected)
    {
        Assert.Equal(expected, FarSpeakerCloneConsent.Fold(name));
    }

    [Fact]
    public void TheConsentFieldIsTheSha256OfTheStandInIdAndTheFoldedName()
    {
        var field = FarSpeakerCloneConsent.ConsentField("  TÚ Huỳnh ");

        Assert.Equal(Sha256Hex($"{StandIn}:tú huỳnh"), field);
        Assert.Equal(64, field!.Length);
        Assert.Equal(field.ToLowerInvariant(), field);
    }

    /// <summary>
    /// Digests produced by the Python twin itself —
    /// <c>hashlib.sha256(f"{id}:{' '.join(unicodedata.normalize('NFKC', n).lower().split())}".encode())</c>
    /// — rather than by this code, so a drift between the two languages fails here and not in a
    /// meeting where a consented voice quietly is not cloned.
    /// </summary>
    [Theory]
    [InlineData("Trần  An", "7f9a7e8a6581e6d14caeeb07c98251f632e5672850fa15df07d344e2aa1f082b")]
    [InlineData("  TÚ Huỳnh ", "91415b813893f5a9e0af6bdf5017715d4df0364ee90f3d347beaa591a0e1539e")]
    [InlineData("Ａｎ Ｎｇｕｙｅｎ", "51d7200e70e63c22898bf8a7d2bb15383e690d4405add8176ce3828497ffdb36")]
    public void TheConsentFieldMatchesWhatTheTtsWorkerComputes(string name, string expected)
    {
        Assert.Equal(expected, FarSpeakerCloneConsent.ConsentField(name));
    }

    [Fact]
    public void DifferentSpellingsOfOneNameShareOneField()
    {
        Assert.Equal(
            FarSpeakerCloneConsent.ConsentField("Trần An"),
            FarSpeakerCloneConsent.ConsentField("  trần   AN "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void ANameThatFoldsToNothingHasNoField(string? name)
    {
        Assert.Null(FarSpeakerCloneConsent.ConsentField(name));
    }

    [Fact]
    public void TheKeyIsTheRoomIdAsALowerCaseHyphenatedGuid()
    {
        Assert.Equal(
            "translationRoom:3f2b6c1e-9a4d-4e7b-8c21-5d0a7e9f1b34:far_speaker_clone_consents",
            FarSpeakerCloneConsent.KeyFor(Guid.Parse("3F2B6C1E-9A4D-4E7B-8C21-5D0A7E9F1B34")));
    }

    // ── PUT: the host ticks and unticks one name ─────────────────────────────────────────────

    [Fact]
    public async Task TheHostTicksANameAndTheFieldIsWrittenWithADayToLive()
    {
        RoomIs();

        var result = await _service.SetAsync(RoomId, HostId, Name, consented: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(Name, result.Value!.DisplayName);
        Assert.True(result.Value.Consented);

        var field = Assert.Single(_redis.Hash(Key));
        Assert.Equal(Sha256Hex($"{StandIn}:trần an"), field.Key);

        // ISO-8601, UTC.
        var at = DateTimeOffset.Parse(field.Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.EndsWith("Z", field.Value);
        Assert.InRange(at, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(TimeSpan.FromHours(24), _redis.Expiries[Key]);
    }

    [Fact]
    public async Task TickingTwiceIsOneConsentAndKeepsTheMomentItWasFirstGiven()
    {
        RoomIs();

        await _service.SetAsync(RoomId, HostId, Name, consented: true);
        var first = _redis.Hash(Key).Single().Value;
        _redis.Hash(Key)[_redis.Hash(Key).Single().Key] = "2026-10-03T01:02:03.0000000Z";

        var again = await _service.SetAsync(RoomId, HostId, " trần an ", consented: true);

        Assert.True(again.IsSuccess);
        Assert.True(again.Value!.Consented);
        Assert.NotEqual(string.Empty, first);
        Assert.Equal("2026-10-03T01:02:03.0000000Z", Assert.Single(_redis.Hash(Key)).Value);
    }

    [Fact]
    public async Task TheHostUnticksANameAndTheFieldIsRemoved()
    {
        RoomIs();
        await _service.SetAsync(RoomId, HostId, Name, consented: true);
        await _service.SetAsync(RoomId, HostId, "Tú Huỳnh", consented: true);

        var result = await _service.SetAsync(RoomId, HostId, Name, consented: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(Name, result.Value!.DisplayName);
        Assert.False(result.Value.Consented);

        // Only that person's field: the other consent is untouched.
        var left = Assert.Single(_redis.Hash(Key));
        Assert.Equal(FarSpeakerCloneConsent.ConsentField("Tú Huỳnh"), left.Key);
    }

    [Fact]
    public async Task UntickingANameThatWasNeverTickedIsStillASuccess()
    {
        RoomIs();

        var once = await _service.SetAsync(RoomId, HostId, Name, consented: false);
        var twice = await _service.SetAsync(RoomId, HostId, Name, consented: false);

        Assert.True(once.IsSuccess);
        Assert.True(twice.IsSuccess);
        Assert.False(twice.Value!.Consented);
        Assert.Empty(_redis.Hash(Key));
    }

    [Fact]
    public async Task TheTransfereeIsTheHostAndTheBookerNoLongerIs()
    {
        RoomIs(activeHostId: GuestId);

        var transferee = await _service.SetAsync(RoomId, GuestId, Name, consented: true);
        var booker = await _service.SetAsync(RoomId, HostId, "Tú Huỳnh", consented: true);

        Assert.True(transferee.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, booker.ErrorCode);
        Assert.Single(_redis.Hash(Key));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SomeoneWhoIsNotTheHostIsRefused(bool consented)
    {
        RoomIs();
        await _service.SetAsync(RoomId, HostId, Name, consented: true);

        var result = await _service.SetAsync(RoomId, GuestId, Name, consented);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(FarSpeakerCloneConsent.ErrorOnlyHostCanRecord, result.Error);
        // Neither added nor — the one that matters — withdrawn by somebody else.
        Assert.Single(_redis.Hash(Key));
    }

    [Fact]
    public async Task ANativeRoomIsRefused()
    {
        RoomIs(type: TranslationRoomTypes.Event);

        var result = await _service.SetAsync(RoomId, HostId, Name, consented: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        Assert.Equal(BridgeRoomConstants.ErrorNotABridgeRoom, result.Error);
        Assert.Empty(_redis.Hash(Key));
    }

    [Theory]
    [InlineData("ENDED")]
    [InlineData("CANCELLED")]
    [InlineData("EXPIRED")]
    [InlineData("FAILED")]
    public async Task ARoomThatHasEndedIsRefused(string status)
    {
        RoomIs(status: status);

        var result = await _service.SetAsync(RoomId, HostId, Name, consented: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        Assert.Equal(BridgeRoomConstants.ErrorRoomClosed, result.Error);
        Assert.Empty(_redis.Hash(Key));
    }

    [Fact]
    public async Task ARoomThatDoesNotExistIsNotFound()
    {
        var result = await _service.SetAsync(RoomId, HostId, Name, consented: true);

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　\t")] // an ideographic space is still nobody's name
    public async Task ABlankNameIsAValidationError(string? name)
    {
        RoomIs();

        var result = await _service.SetAsync(RoomId, HostId, name, consented: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(400, ApiErrorStatus.For(result.ErrorCode));
        Assert.Empty(_redis.Hash(Key));
    }

    [Fact]
    public async Task ANameLongerThanAHundredCharsIsAValidationErrorAndAHundredIsNot()
    {
        RoomIs();

        var tooLong = await _service.SetAsync(RoomId, HostId, new string('a', 101), consented: true);
        var longest = await _service.SetAsync(RoomId, HostId, new string('a', 100), consented: true);

        Assert.Equal(ErrorCodes.ValidationError, tooLong.ErrorCode);
        Assert.Equal(400, ApiErrorStatus.For(tooLong.ErrorCode));
        Assert.True(longest.IsSuccess);
        Assert.Single(_redis.Hash(Key));
    }

    [Fact]
    public async Task AWriteThatFailsIsReportedAndNotAnsweredAsDone()
    {
        RoomIs();
        _redis.Broken = true;

        var result = await _service.SetAsync(RoomId, HostId, Name, consented: false);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
    }

    // ── status: which of these names are consented ───────────────────────────────────────────

    [Fact]
    public async Task StatusAnswersExactlyTheConsentedSubsetSpelledAsSubmitted()
    {
        RoomIs();
        await _service.SetAsync(RoomId, HostId, "Trần An", consented: true);
        await _service.SetAsync(RoomId, HostId, "Tú Huỳnh", consented: true);
        await _service.SetAsync(RoomId, HostId, "Lê Bình", consented: true);
        await _service.SetAsync(RoomId, HostId, "Lê Bình", consented: false);

        var result = await _service.GetStatusAsync(
            RoomId, HostId, ["  TRẦN   An ", "Lê Bình", "Nobody Asked", "tú huỳnh", "", null]);

        Assert.True(result.IsSuccess);
        // As submitted — not folded, not the spelling it was recorded under — and in order.
        Assert.Equal(["  TRẦN   An ", "tú huỳnh"], result.Value!.Consented);
    }

    [Fact]
    public async Task StatusOfARoomNobodyHasTickedIsAnEmptyList()
    {
        RoomIs();

        var result = await _service.GetStatusAsync(RoomId, HostId, ["Trần An"]);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Consented);
    }

    [Fact]
    public async Task StatusOfMoreThanFiftyNamesIsAValidationErrorAndFiftyIsNot()
    {
        RoomIs();
        var names = Enumerable.Range(0, 51).Select(i => $"Person {i}").ToList();

        var tooMany = await _service.GetStatusAsync(RoomId, HostId, names);
        var most = await _service.GetStatusAsync(RoomId, HostId, names.Take(50).ToList());

        Assert.Equal(ErrorCodes.ValidationError, tooMany.ErrorCode);
        Assert.Equal(400, ApiErrorStatus.For(tooMany.ErrorCode));
        Assert.True(most.IsSuccess);
    }

    [Fact]
    public async Task StatusWithoutAListIsAValidationError()
    {
        RoomIs();

        var result = await _service.GetStatusAsync(RoomId, HostId, null);

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task StatusIsTheHostsToAskToo()
    {
        RoomIs();
        await _service.SetAsync(RoomId, HostId, Name, consented: true);

        var guest = await _service.GetStatusAsync(RoomId, GuestId, [Name]);

        Assert.False(guest.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, guest.ErrorCode);
    }

    [Fact]
    public async Task StatusOfANativeOrEndedRoomIsRefused()
    {
        RoomIs(type: TranslationRoomTypes.Event);
        var native = await _service.GetStatusAsync(RoomId, HostId, [Name]);

        RoomIs(status: "ENDED");
        var ended = await _service.GetStatusAsync(RoomId, HostId, [Name]);

        Assert.Equal(ErrorCodes.InvalidState, native.ErrorCode);
        Assert.Equal(ErrorCodes.InvalidState, ended.ErrorCode);
    }

    [Fact]
    public async Task AStatusThatCannotBeReadIsAnErrorAndNotAnEmptyList()
    {
        RoomIs();
        _redis.Broken = true;

        var result = await _service.GetStatusAsync(RoomId, HostId, [Name]);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
    }

    // ── the name is never kept ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDisplayNameIsNeitherStoredNorLogged()
    {
        RoomIs();
        const string name = "Nguyễn Thị Minh Khai";
        var field = FarSpeakerCloneConsent.ConsentField(name)!;

        await _service.SetAsync(RoomId, HostId, name, consented: true);
        await _service.GetStatusAsync(RoomId, HostId, [name]);
        await _service.SetAsync(RoomId, HostId, name, consented: false);
        _redis.Broken = true;
        await _service.SetAsync(RoomId, HostId, name, consented: true);
        await _service.GetStatusAsync(RoomId, HostId, [name]);

        // Everything that ever went to Redis, including what was later removed.
        Assert.NotEmpty(_redis.EverWritten);
        Assert.All(_redis.EverWritten, written => AssertNoName(written, name));

        // One line per write and one per withdrawal, each carrying the room, the host and the
        // first 12 hex chars of the field — and nothing longer of it.
        var audit = _logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        Assert.Equal(2, audit.Count);
        Assert.Contains("RECORDED", audit[0].Message);
        Assert.Contains("WITHDRAWN", audit[1].Message);
        Assert.All(_logger.Entries, entry =>
        {
            AssertNoName(entry.Message, name);
            Assert.Contains(RoomId.ToString(), entry.Message);
            Assert.DoesNotContain(field, entry.Message);
            Assert.DoesNotContain(field[..13], entry.Message);
        });
        Assert.All(audit, entry =>
        {
            Assert.Contains(HostId.ToString(), entry.Message);
            Assert.Contains(field[..12], entry.Message);
        });
    }

    private static void AssertNoName(string text, string name)
    {
        Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FarSpeakerCloneConsent.Fold(name), text, StringComparison.OrdinalIgnoreCase);
        foreach (var word in name.Split(' '))
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Redis hashes with their real semantics — HSETNX loses to a field that is there, HDEL says
    /// whether there was one, an emptied hash stops existing — because idempotency is exactly
    /// what these tests are about, and a mock that returns what it is told cannot show it.
    /// The shared InMemoryRedisState models strings and streams and answers hashes with nothing.
    /// </summary>
    private sealed class HashRedis : IRedisStateRepository
    {
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new();

        public Dictionary<string, TimeSpan> Expiries { get; } = new();

        /// <summary>Every key, field and value that was ever sent, whatever became of it.</summary>
        public List<string> EverWritten { get; } = [];

        public bool Broken { get; set; }

        public Dictionary<string, string> Hash(string key) =>
            _hashes.TryGetValue(key, out var hash) ? hash : new Dictionary<string, string>();

        private void Touch(params string[] sent)
        {
            if (Broken) throw new InvalidOperationException("Redis is down.");
            EverWritten.AddRange(sent);
        }

        public Task<bool> HashSetIfAbsentAsync(string key, string field, string value)
        {
            Touch(key, field, value);
            if (!_hashes.TryGetValue(key, out var hash))
                _hashes[key] = hash = new Dictionary<string, string>();
            return Task.FromResult(hash.TryAdd(field, value));
        }

        public Task HashSetAsync(string key, Dictionary<string, string> fields)
        {
            foreach (var (field, value) in fields)
            {
                Touch(key, field, value);
                if (!_hashes.TryGetValue(key, out var hash))
                    _hashes[key] = hash = new Dictionary<string, string>();
                hash[field] = value;
            }

            return Task.CompletedTask;
        }

        public Task<bool> HashDeleteAsync(string key, string field)
        {
            Touch(key, field);
            if (!_hashes.TryGetValue(key, out var hash) || !hash.Remove(field))
                return Task.FromResult(false);
            if (hash.Count == 0) _hashes.Remove(key);
            return Task.FromResult(true);
        }

        public Task<string?> HashGetAsync(string key, string field)
        {
            Touch(key, field);
            return Task.FromResult(Hash(key).TryGetValue(field, out var value) ? value : null);
        }

        public Task<Dictionary<string, string>> GetHashAllAsync(string key)
        {
            Touch(key);
            return Task.FromResult(new Dictionary<string, string>(Hash(key), StringComparer.OrdinalIgnoreCase));
        }

        public Task<bool> KeyExpireAsync(string key, TimeSpan expiry)
        {
            Touch(key);
            if (!_hashes.ContainsKey(key)) return Task.FromResult(false);
            Expiries[key] = expiry;
            return Task.FromResult(true);
        }

        public Task<bool> KeyDeleteAsync(string key) => throw new NotSupportedException();
        public Task<bool> WaitForSignalAsync(string channel, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> StringSetAsync(string key, string value, TimeSpan? expiry = null) => throw new NotSupportedException();
        public Task<bool> StringSetIfAbsentAsync(string key, string value, TimeSpan expiry) => throw new NotSupportedException();
        public Task<bool> KeyDeleteIfEqualsAsync(string key, string expectedValue) => throw new NotSupportedException();
        public Task<string?> StringGetAsync(string key) => throw new NotSupportedException();
        public Task<long> PublishAsync(string channel, string message) => throw new NotSupportedException();
        public Task<string> StreamAddAsync(string stream, Dictionary<string, string> fields) => throw new NotSupportedException();
    }

    /// <summary>A real ILogger: what is asserted is the text that would reach the log.</summary>
    private sealed class RecordingLogger : ILogger<BridgeVoiceCloneConsentService>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }
}
