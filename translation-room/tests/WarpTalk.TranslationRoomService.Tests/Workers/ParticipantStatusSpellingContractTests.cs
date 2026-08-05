using System.Text.RegularExpressions;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// WT-263: participant status has exactly one spelling in production code —
/// <c>TranslationRoomParticipantStatuses.*</c>.
///
/// The status column is VARCHAR since migration 014, so the CLR enum
/// <c>TranslationRoomParticipantStatus</c> is kept for one job only: it is the <c>nameof</c> source
/// inside the constants class, and it carries the JSON contract shape. Every other site — persistence,
/// comparisons, predicates, mappers — goes through the constants. These tests fail if a raw literal or
/// an <c>enum.ToString()</c> reintroduces a second spelling that can silently drift.
/// </summary>
public sealed class ParticipantStatusSpellingContractTests
{
    private const string SourceRoot = "translation-room/src";

    /// <summary>
    /// The enum may only be named inside the constants class that derives the strings from it.
    /// Matches member access (<c>TranslationRoomParticipantStatus.CONNECTED</c>), which covers both
    /// <c>.ToString()</c> and <c>nameof(...)</c>; the plural constants class does not match because
    /// its name has no dot in that position.
    /// </summary>
    [Fact]
    public void ParticipantStatusEnum_IsNamedOnlyInsideTheConstantsClass()
    {
        var enumMemberAccess = new Regex(@"TranslationRoomParticipantStatus\.[A-Z_]+", RegexOptions.Compiled);

        var offenders = EnumerateProductionSources()
            .Where(file => !file.EndsWith("TranslationRoomParticipantStatuses.cs", StringComparison.Ordinal))
            .Where(file => enumMemberAccess.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The participant-owned sources carry no raw status literal at all. These three files describe
    /// participants only — unlike TranslationRoomService, which legitimately compares ROOM statuses
    /// that share the "WAITING" spelling — so a bare literal here is always the regression.
    /// </summary>
    [Theory]
    [InlineData("WarpTalk.TranslationRoomService.Application/Mappers/TranslationRoomParticipantMapper.cs")]
    [InlineData("WarpTalk.TranslationRoomService.Application/Services/TranslationRoomParticipantService.cs")]
    [InlineData("WarpTalk.TranslationRoomService.API/GrpcServices/TranslationRoomGrpcService.cs")]
    public void ParticipantSources_CarryNoRawStatusLiteral(string relativePath)
    {
        var source = File.ReadAllText(FindPath(Path.Combine(SourceRoot, relativePath)));

        var literals = new[]
        {
            "\"INVITED\"", "\"WAITING\"", "\"CONNECTED\"", "\"DISCONNECTED\"",
            "\"LEFT\"", "\"KICKED\"", "\"REJECTED\""
        };

        var found = literals.Where(literal => source.Contains(literal, StringComparison.Ordinal)).ToList();

        Assert.Empty(found);
    }

    private static IEnumerable<string> EnumerateProductionSources() =>
        Directory
            .EnumerateFiles(FindPath(SourceRoot), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindPath(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException($"Could not locate {relativePath}.");
    }
}
