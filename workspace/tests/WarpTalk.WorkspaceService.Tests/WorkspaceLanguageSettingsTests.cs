using System.Collections.Generic;
using System.Text.Json;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Application.Validators;
using WarpTalk.WorkspaceService.Domain.Settings;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// WT-706. The workspace language whitelist (L1) on the way IN — what a save is allowed to store
/// and what "no restriction" means once it can be said out loud.
///
/// Before this, the settings endpoint stored whatever it was handed: "vi-VN" (which no room can
/// match, because rooms store primary subtags), "VI", "", the same language twice, a code no
/// catalogue has ever held. Every one of those had a consequence a settings screen could not
/// show — a workspace could save a whitelist that made every meeting impossible to create — and
/// emptying the list switched a restricted workspace to unrestricted without saying so.
/// </summary>
public class WorkspaceLanguageSettingsTests
{
    private static readonly IReadOnlyCollection<string> NoDomains = new List<string>();

    private static WorkspaceSettingsDto Settings(
        List<string>? allowedTargetLanguages = null,
        bool? restrictLanguages = null,
        string defaultLanguage = "en") =>
        new(
            defaultLanguage,
            "UTC",
            allowedTargetLanguages ?? new List<string>(),
            true,
            5,
            30,
            new List<string>(),
            true,
            false,
            null,
            false,
            7,
            RestrictLanguages: restrictLanguages);

    // ---------------------------------------------------------------------------------------
    // Normalization
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The case from the audit. A regional tag and a shouted one name the same language a room
    /// stores as "vi", so they collapse into one entry — a workspace whose whitelist read
    /// ["vi-VN"] used to be unable to create any meeting at all.
    ///
    /// "xx" SURVIVES, and that is a deliberate limit rather than an oversight: the platform
    /// catalogue is translation_room.supported_languages, which this service has no client for,
    /// so a well-formed code it has never heard of is accepted here and refused by the
    /// room-creation path that does read the catalogue.
    /// </summary>
    [Fact]
    public void Save_NormalizesRegionalAndCasedTags_AndKeepsWellFormedUnknownCodes()
    {
        var settings = Settings(
            new List<string> { "vi-VN", "VI", "xx" },
            restrictLanguages: true,
            defaultLanguage: "vi");

        Assert.True(WorkspaceSettingsValidator.Validate(settings, NoDomains).IsValid);
        Assert.Equal(new List<string> { "vi", "xx" }, settings.ToConfiguration().AllowedTargetLanguages);
    }

    [Theory]
    [InlineData("vi_VN")]
    [InlineData("  vi  ")]
    [InlineData("vi-Latn-VN")]
    public void Save_ReducesEveryFormOfATagToItsPrimarySubtag(string written)
    {
        var settings = Settings(new List<string> { written }, restrictLanguages: true, defaultLanguage: "vi");

        Assert.True(WorkspaceSettingsValidator.Validate(settings, NoDomains).IsValid);
        Assert.Equal(new List<string> { "vi" }, settings.ToConfiguration().AllowedTargetLanguages);
    }

    [Fact]
    public void Save_DedupesRepeatedLanguages_KeepingTheOrderTheOwnerChose()
    {
        var settings = Settings(
            new List<string> { "ja", "en-US", "ja-JP", "en", "ja" },
            restrictLanguages: true,
            defaultLanguage: "ja");

        Assert.Equal(new List<string> { "ja", "en" }, settings.ToConfiguration().AllowedTargetLanguages);
    }

    /// <summary>
    /// A blank slot is a checkbox list serializing badly, not the owner naming a language, so it
    /// is dropped rather than refused — but it must not be stored either, because "" matches no
    /// room language and would sit in the whitelist forever doing nothing.
    /// </summary>
    [Fact]
    public void Save_DropsBlankEntriesWithoutRefusingTheSave()
    {
        var settings = Settings(new List<string> { "vi", "", "   " }, restrictLanguages: true, defaultLanguage: "vi");

        Assert.True(WorkspaceSettingsValidator.Validate(settings, NoDomains).IsValid);
        Assert.Equal(new List<string> { "vi" }, settings.ToConfiguration().AllowedTargetLanguages);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("v")]
    [InlineData("12")]
    [InlineData("v!")]
    public void Save_RefusesACodeThatIsNotALanguageCode_AndNamesIt(string written)
    {
        var result = WorkspaceSettingsValidator.Validate(
            Settings(new List<string> { "vi", written }, restrictLanguages: true, defaultLanguage: "vi"),
            NoDomains);

        Assert.False(result.IsValid);
        Assert.Contains("allowedTargetLanguages", result.Errors.Keys);
        // The refusal quotes what the owner typed, not what it reduced to — the point of the
        // whole check is that they can find the offending entry on their own screen.
        Assert.Contains(written.Trim(), result.ErrorMessage);
    }

    // ---------------------------------------------------------------------------------------
    // The Allow-all switch
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The bug the switch exists for: unticking the last language used to leave an empty list,
    /// which every reader — this service, translation-room, the gateway — takes as "allow
    /// everything". A restriction cannot narrow to nothing, so it is refused and the owner is
    /// told which of the two things they meant.
    /// </summary>
    [Fact]
    public void RestrictedWithAnEmptyList_IsRefused()
    {
        var result = WorkspaceSettingsValidator.Validate(
            Settings(new List<string>(), restrictLanguages: true),
            NoDomains);

        Assert.False(result.IsValid);
        Assert.Contains("allowedTargetLanguages", result.Errors.Keys);
    }

    [Fact]
    public void RestrictedWithNothingButBlanks_IsRefusedTheSameWay()
    {
        var result = WorkspaceSettingsValidator.Validate(
            Settings(new List<string> { "", "  " }, restrictLanguages: true),
            NoDomains);

        Assert.False(result.IsValid);
        Assert.Contains("allowedTargetLanguages", result.Errors.Keys);
    }

    /// <summary>
    /// Turning the restriction OFF stores an empty list, and it has to: PATCH merges over the
    /// document GET returned, so "allow all languages" arrives as restrictLanguages:false on top
    /// of whatever list the workspace currently has. The empty list is also what keeps "empty
    /// means unrestricted" true for the gRPC settings response nobody else was asked to change.
    /// </summary>
    [Fact]
    public void TurningTheRestrictionOff_ClearsTheListRatherThanRefusingIt()
    {
        var settings = Settings(new List<string> { "vi", "en" }, restrictLanguages: false);

        Assert.True(WorkspaceSettingsValidator.Validate(settings, NoDomains).IsValid);

        var config = settings.ToConfiguration();
        Assert.False(config.RestrictLanguages);
        Assert.Empty(config.AllowedTargetLanguages);
    }

    [Fact]
    public void TurningTheRestrictionOff_ServesAnEmptyListOnTheNextRead()
    {
        var stored = Settings(new List<string> { "vi", "en" }, restrictLanguages: false).ToConfiguration();

        var served = stored.ToSettingsDto();

        Assert.False(served.RestrictLanguages);
        Assert.Empty(served.AllowedTargetLanguages);
    }

    /// <summary>
    /// An unrestricted workspace is not asked to justify entries that are about to be discarded.
    /// Refusing a stale "vi-VN" inside the very save that switches the workspace to "allow all"
    /// would block the only operation that makes it irrelevant.
    /// </summary>
    [Fact]
    public void AnUnrestrictedSave_IsNotValidatedAgainstTheListItIsAboutToDrop()
    {
        var result = WorkspaceSettingsValidator.Validate(
            Settings(new List<string> { "english", "" }, restrictLanguages: false, defaultLanguage: "ko"),
            NoDomains);

        Assert.True(result.IsValid);
    }

    // ---------------------------------------------------------------------------------------
    // DefaultLanguage
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A default outside the whitelist can only ever produce an error: every meeting it seeds is
    /// refused at creation by the workspace's own policy.
    /// </summary>
    [Fact]
    public void DefaultLanguageOutsideTheWhitelist_IsRefused()
    {
        var result = WorkspaceSettingsValidator.Validate(
            Settings(new List<string> { "vi", "en" }, restrictLanguages: true, defaultLanguage: "ko"),
            NoDomains);

        Assert.False(result.IsValid);
        Assert.Contains("defaultLanguage", result.Errors.Keys);
    }

    /// <summary>The default is compared normalized too, or "en-US" would be outside ["en"].</summary>
    [Fact]
    public void DefaultLanguageIsComparedOnItsPrimarySubtag()
    {
        var result = WorkspaceSettingsValidator.Validate(
            Settings(new List<string> { "en", "vi" }, restrictLanguages: true, defaultLanguage: "en-US"),
            NoDomains);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void DefaultLanguageIsNotCheckedWhileEveryLanguageIsAllowed()
    {
        var result = WorkspaceSettingsValidator.Validate(
            Settings(new List<string>(), restrictLanguages: false, defaultLanguage: "ko"),
            NoDomains);

        Assert.True(result.IsValid);
    }

    // ---------------------------------------------------------------------------------------
    // Back-compat: documents and clients written before WT-706
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Every settings document stored before WT-706 lacks the flag, so it is derived exactly as
    /// the old readers read the list: entries mean restricted, none means allow all. No data
    /// migration, and no workspace changes behaviour by being read.
    /// </summary>
    [Theory]
    [InlineData("{\"AllowedTargetLanguages\":[\"vi\",\"en\"]}", true)]
    [InlineData("{\"AllowedTargetLanguages\":[]}", false)]
    [InlineData("{}", false)]
    public void AStoredDocumentWithNoFlag_DerivesTheRestrictionFromItsList(string json, bool expectedRestricted)
    {
        var config = JsonSerializer.Deserialize<WorkspaceConfiguration>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(expectedRestricted, config.RestrictLanguages);
    }

    [Fact]
    public void AStoredDocumentWithNoFlagAndNoList_KeepsServingAnEmptyWhitelist()
    {
        var config = JsonSerializer.Deserialize<WorkspaceConfiguration>("{\"AllowedTargetLanguages\":[]}")!;

        // Empty is what ValidateMeetingCreationAsync, the gRPC settings response and the gateway
        // all read as "unrestricted". Nothing about that reading may change for existing data.
        Assert.Empty(config.AllowedTargetLanguages);
        Assert.False(config.RestrictLanguages);
    }

    [Fact]
    public void AStoredDocumentWithNoFlagAndAList_KeepsServingThatList()
    {
        var config = JsonSerializer.Deserialize<WorkspaceConfiguration>(
            "{\"AllowedTargetLanguages\":[\"vi\",\"en\"]}")!;

        Assert.Equal(new List<string> { "vi", "en" }, config.AllowedTargetLanguages);
    }

    /// <summary>
    /// A client that has never heard of the flag — anything written before WT-706 — sends the
    /// settings document without it, and gets the behaviour it has always had.
    /// </summary>
    [Fact]
    public void AClientThatOmitsTheFlag_IsReadTheWayItAlwaysWas()
    {
        var restricting = Settings(new List<string> { "vi-VN" }, restrictLanguages: null).ToConfiguration();
        Assert.True(restricting.RestrictLanguages);
        Assert.Equal(new List<string> { "vi" }, restricting.AllowedTargetLanguages);

        var unrestricting = Settings(new List<string>(), restrictLanguages: null).ToConfiguration();
        Assert.False(unrestricting.RestrictLanguages);
        Assert.Empty(unrestricting.AllowedTargetLanguages);
    }

    /// <summary>
    /// The derived answer is what gets written back, so the flag becomes explicit on the first
    /// save after WT-706 and stops being derived from that point on.
    /// </summary>
    [Fact]
    public void SavingMakesTheDerivedFlagExplicitInTheStoredDocument()
    {
        var config = Settings(new List<string> { "vi" }, restrictLanguages: null).ToConfiguration();

        var json = JsonSerializer.Serialize(config);
        var reloaded = JsonSerializer.Deserialize<WorkspaceConfiguration>(json)!;

        Assert.Contains("\"RestrictLanguages\":true", json);
        Assert.True(reloaded.RestrictLanguages);
        Assert.Equal(new List<string> { "vi" }, reloaded.AllowedTargetLanguages);
    }

    /// <summary>
    /// The invariant the untouched consumers depend on, defended at the last possible moment: a
    /// document claiming no restriction never serves a whitelist, however it came to hold one.
    /// </summary>
    [Fact]
    public void ADocumentClaimingNoRestriction_NeverServesAWhitelist()
    {
        var config = JsonSerializer.Deserialize<WorkspaceConfiguration>(
            "{\"RestrictLanguages\":false,\"AllowedTargetLanguages\":[\"vi\"]}")!;

        Assert.Empty(config.AllowedTargetLanguages);
    }
}
