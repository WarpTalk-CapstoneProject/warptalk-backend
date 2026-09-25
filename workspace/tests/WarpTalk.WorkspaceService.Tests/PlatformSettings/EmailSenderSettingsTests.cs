using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Interfaces;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.Shared.Services;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.PlatformSettings;

/// <summary>
/// notifications.email.* reach the shared Resend client (auth and workspace e-mail): what is sent
/// to Resend carries the live sender and reply-to, per message, with the deploy configuration as
/// the fallback.
/// </summary>
public sealed class EmailSenderSettingsTests
{
    [Fact]
    public async Task The_shared_resend_client_sends_from_the_live_sender_with_reply_to()
    {
        var source = new InMemoryPlatformSettingsSource();
        var reader = new PlatformSettingsReader(source, NullLogger<PlatformSettingsReader>.Instance, cacheTtl: TimeSpan.Zero);
        var handler = new CapturingHandler();
        var client = new ResendEmailClient(
            new HttpClient(handler),
            Options.Create(new ResendSettings { ApiKey = "re_test", FromName = "WarpTalk", FromEmail = "no-reply@warptalk.vn" }),
            NullLogger<ResendEmailClient>.Instance,
            reader);
        var request = new SendEmailRequest(string.Empty, "user@example.com", "Hi", "<p>Hi</p>", "Hi");

        await client.SendEmailAsync(request);
        var before = handler.Bodies[^1];
        Assert.Equal("WarpTalk <no-reply@warptalk.vn>", before.GetProperty("from").GetString());
        Assert.False(before.TryGetProperty("reply_to", out _));

        source.Set(PlatformSettingsCatalog.EmailFromName, "WarpTalk Support")
              .Set(PlatformSettingsCatalog.EmailFromAddress, "hello@warptalk.vn")
              .Set(PlatformSettingsCatalog.EmailReplyTo, "support@warptalk.vn");
        await client.SendEmailAsync(request);
        var after = handler.Bodies[^1];
        Assert.Equal("WarpTalk Support <hello@warptalk.vn>", after.GetProperty("from").GetString());
        Assert.Equal("support@warptalk.vn", after.GetProperty("reply_to").GetString());
    }

    [Theory]
    [InlineData("WarpTalk <onboarding@resend.dev>", "WarpTalk", "onboarding@resend.dev")]
    [InlineData("\"Warp Talk\" <a@b.co>", "Warp Talk", "a@b.co")]
    [InlineData("a@b.co", "WarpTalk", "a@b.co")]
    public void Configured_senders_parse_into_name_and_address(string configured, string name, string address)
        => Assert.Equal((name, address), EmailSenderSettings.Parse(configured));

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Bodies.Add(document.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"msg_1\"}") };
        }
    }
}
