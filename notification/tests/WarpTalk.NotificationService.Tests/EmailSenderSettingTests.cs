using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Resend;
using WarpTalk.NotificationService.Infrastructure.Services;
using WarpTalk.Shared.PlatformSettings;
using Xunit;

namespace WarpTalk.NotificationService.Tests;

/// <summary>
/// The notification service's own Resend path reads notifications.email.* per message: the
/// message handed to Resend follows the setting, with Resend:FromEmail as the fallback.
/// </summary>
public sealed class EmailSenderSettingTests
{
    [Fact]
    public async Task The_sender_and_reply_to_follow_the_live_settings()
    {
        var source = new InMemoryPlatformSettingsSource();
        var reader = new PlatformSettingsReader(source, NullLogger<PlatformSettingsReader>.Instance, cacheTtl: TimeSpan.Zero);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Resend:FromEmail"] = "WarpTalk <notify@warptalk.vn>" })
            .Build();
        var sender = new ResendEmailSender(new Mock<IResend>().Object, configuration, NullLogger<ResendEmailSender>.Instance, reader);
        var message = new WarpTalk.NotificationService.Application.Interfaces.EmailMessage("user@example.com", "Hi", "<p>Hi</p>");

        var before = await sender.ComposeAsync(message);
        Assert.Equal("WarpTalk <notify@warptalk.vn>", before.From!.ToString());
        Assert.Null(before.ReplyTo);

        source.Set(PlatformSettingsCatalog.EmailFromName, "WarpTalk Team")
              .Set(PlatformSettingsCatalog.EmailReplyTo, "support@warptalk.vn");
        var after = await sender.ComposeAsync(message);
        Assert.Equal("WarpTalk Team <notify@warptalk.vn>", after.From!.ToString());
        Assert.Equal("support@warptalk.vn", Assert.Single(after.ReplyTo!).Email);
    }
}
