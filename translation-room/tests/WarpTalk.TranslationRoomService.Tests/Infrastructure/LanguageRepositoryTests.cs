using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// Runs against a real Postgres because the bug these cover lived entirely in the gap between
/// the C# lookup and the SQL catalog — every existing test mocks ILanguagePolicy, so nothing
/// crossed that boundary and production broke while the suite stayed green.
///
/// The catalog is seeded the way production is (locale tags, per
/// 20260730141000_seed_supported_languages.sql) while callers pass the bare code they get out
/// of LanguageHelper.NormalizeLanguageCode. That mismatch is the whole defect.
/// </summary>
public class LanguageRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private TranslationRoomDbContext _dbContext = null!;
    private LanguageRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        var options = new DbContextOptionsBuilder<TranslationRoomDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;
        _dbContext = new TranslationRoomDbContext(options);

        await _dbContext.Database.ExecuteSqlRawAsync(
            "CREATE SCHEMA IF NOT EXISTS translation_room;");
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS translation_room.supported_languages (
                code VARCHAR(15) PRIMARY KEY,
                name VARCHAR(100) NOT NULL,
                native_name VARCHAR(100),
                is_active BOOLEAN NOT NULL DEFAULT TRUE
            );
            """);
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO translation_room.supported_languages (code, name, native_name, is_active)
            VALUES
                ('vi-VN', 'Vietnamese', 'Tiếng Việt', TRUE),
                ('en-US', 'English', 'English', TRUE),
                ('ja-JP', 'Japanese', '日本語', TRUE),
                ('ko-KR', 'Korean', '한국어', TRUE),
                ('de-DE', 'German', 'Deutsch', FALSE)
            ON CONFLICT (code) DO NOTHING;
            """);

        _repository = new LanguageRepository(_dbContext);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    public async Task BareCode_IsSupported_AgainstALocaleTaggedCatalog(string code)
    {
        // The regression: TranslationRoomService and LanguagePolicy normalize to the bare code
        // before asking, so this is the only shape the catalog is ever queried with in
        // production. An exact match against 'vi-VN' never hit and room creation, room update
        // and room join all failed with "Source language is not supported."
        Assert.True(await _repository.IsSupportedAsync(code));
    }

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public async Task LocaleTag_IsStillSupported(string code)
    {
        // Callers that have not been through NormalizeLanguageCode — and any future re-seed of
        // the catalog in bare form — must keep working too.
        Assert.True(await _repository.IsSupportedAsync(code));
    }

    [Theory]
    [InlineData("VI")]
    [InlineData("En-Us")]
    [InlineData("  vi-VN  ")]
    public async Task MatchingIgnoresCasingAndSurroundingWhitespace(string code)
    {
        Assert.True(await _repository.IsSupportedAsync(code));
    }

    [Fact]
    public async Task AnInactiveLanguageIsNotSupported()
    {
        // is_active is how a language is withdrawn without deleting history; matching on the
        // primary subtag must not quietly reinstate it.
        Assert.False(await _repository.IsSupportedAsync("de"));
        Assert.False(await _repository.IsSupportedAsync("de-DE"));
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("xx-XX")]
    [InlineData("klingon")]
    public async Task AnUnknownLanguageIsNotSupported(string code)
    {
        Assert.False(await _repository.IsSupportedAsync(code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    public async Task AnEmptyOrDegenerateCodeMatchesNothing(string code)
    {
        // "-" is the interesting one: it normalizes to an empty primary subtag, and a naive
        // StartsWith on that prefix would match every row in the catalog.
        Assert.False(await _repository.IsSupportedAsync(code));
    }
}
