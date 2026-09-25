using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Tests.Infrastructure;

/// <summary>
/// NotificationDbContext hand-maps every column; a property left to EF's default is sent as
/// PascalCase and 500s every query over its table. The v3 tables are held to snake_case here.
/// </summary>
public sealed class EmailCmsV3MappingTests
{
    public static TheoryData<Type> Entities() =>
        [typeof(EmailCustomTemplate), typeof(EmailCampaign), typeof(EmailCampaignRecipient), typeof(Announcement)];

    [Theory]
    [MemberData(nameof(Entities))]
    public void EveryColumn_IsSnakeCase(Type type)
    {
        using var context = new NotificationDbContext(
            new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
        var entity = context.Model.FindEntityType(type)!;

        Assert.All(entity.GetProperties(), property =>
            Assert.Matches(new Regex("^[a-z][a-z0-9_]*$"), property.GetColumnName()));
    }

    [Fact]
    public void TheEmailChannelColumns_AreTheMigrations()
    {
        using var context = new NotificationDbContext(
            new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
        var announcement = context.Model.FindEntityType(typeof(Announcement))!;

        Assert.Equal("email_template_key", announcement.FindProperty(nameof(Announcement.EmailTemplateKey))!.GetColumnName());
        Assert.Equal("email_campaign_id", announcement.FindProperty(nameof(Announcement.EmailCampaignId))!.GetColumnName());
    }
}
