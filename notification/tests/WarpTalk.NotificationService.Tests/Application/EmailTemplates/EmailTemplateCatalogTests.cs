using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Tests.Application.EmailTemplates;

/// <summary>The built-in wording and the shared renderer every sender, preview and test email use.</summary>
public sealed class EmailTemplateCatalogTests
{
    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var definition in EmailTemplateCatalog.All) data.Add(definition.Key);
        return data;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void EveryDefault_PassesTheSameValidationAnAdminEditMust(string key)
    {
        var definition = EmailTemplateCatalog.Get(key);

        Assert.Empty(EmailTemplateRenderer.Validate(definition, definition.Default));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void EveryDefault_RendersWithNoPlaceholderLeftUnfilled(string key)
    {
        var definition = EmailTemplateCatalog.Get(key);

        var email = EmailTemplateRenderer.Render(definition, definition.Default, EmailTemplateCatalog.SampleValues(definition));

        Assert.DoesNotContain("{{", email.Subject + email.HtmlBody + email.TextBody);
        Assert.StartsWith("<!DOCTYPE html>", email.HtmlBody);
        Assert.False(string.IsNullOrWhiteSpace(email.TextBody));
    }

    [Fact]
    public void Keys_AreUnique_AndEveryEmailHasOneRequiredLinkAtMost()
    {
        Assert.Equal(EmailTemplateCatalog.All.Count, EmailTemplateCatalog.All.Select(d => d.Key).Distinct().Count());
        Assert.All(EmailTemplateCatalog.All, d => Assert.True(d.Variables.Count(v => v.Required) <= 1));
    }

    [Theory]
    [InlineData(EmailTemplateCatalog.AuthVerifyEmail, "Resend")]
    [InlineData(EmailTemplateCatalog.AuthPasswordReset, "Resend")]
    [InlineData(EmailTemplateCatalog.WorkspaceInvitation, "Resend")]
    [InlineData(EmailTemplateCatalog.WorkspaceJoinRequestApproved, "Resend")]
    [InlineData(EmailTemplateCatalog.NotificationEmailCopy, "Resend")]
    [InlineData(EmailTemplateCatalog.MeetingInvitation, "SMTP")]
    [InlineData(EmailTemplateCatalog.MeetingReminder, "SMTP")]
    public void EachEmail_NamesTheProviderItsSenderUses(string key, string provider)
    {
        // Both send paths read the stored template: ResendAuthEmailSenderTemplateTests and
        // WorkspaceInvitationEmailComposerTests prove the Resend one, SmtpEmailServiceEncodingTests
        // the SMTP one.
        Assert.Equal(provider, EmailTemplateCatalog.Get(key).Provider);
    }

    [Fact]
    public void Render_EncodesValuesInTheBodyAndHeading_ButNotInTheSubject()
    {
        var definition = EmailTemplateCatalog.Get(EmailTemplateCatalog.MeetingInvitation);
        var content = new EmailTemplateContent("Invite: {{MeetingTitle}}", "About {{MeetingTitle}}", "<p>{{MeetingTitle}}</p><a href=\"{{MeetingLink}}\">Join</a>");

        var email = EmailTemplateRenderer.Render(definition, content, new Dictionary<string, string>
        {
            ["MeetingTitle"] = "<b>Q3</b> & \"more\"",
            ["MeetingLink"] = "https://x.test/?a=1&b=2",
        });

        Assert.Equal("Invite: <b>Q3</b> & \"more\"", email.Subject);
        Assert.Contains("<p>&lt;b&gt;Q3&lt;/b&gt; &amp; &quot;more&quot;</p>", email.HtmlBody);
        Assert.Contains("About &lt;b&gt;Q3&lt;/b&gt; &amp; &quot;more&quot;", email.HtmlBody);
        Assert.Contains("href=\"https://x.test/?a=1&amp;b=2\"", email.HtmlBody);
        Assert.Contains("Join (https://x.test/?a=1&b=2)", email.TextBody);
    }

    [Fact]
    public void Render_FoldsLineBreaksInASubjectValue()
    {
        var definition = EmailTemplateCatalog.Get(EmailTemplateCatalog.MeetingInvitation);

        var email = EmailTemplateRenderer.Render(definition, definition.Default, new Dictionary<string, string>
        {
            ["MeetingTitle"] = "Sync\r\nBcc: x@evil.test",
            ["MeetingLink"] = "https://x.test",
        });

        Assert.DoesNotContain('\n', email.Subject);
        Assert.DoesNotContain('\r', email.Subject);
    }

    [Fact]
    public void Validate_FlagsMalformedPlaceholdersAndLineBreaksInTheSubject()
    {
        var definition = EmailTemplateCatalog.Get(EmailTemplateCatalog.AuthVerifyEmail);

        var issues = EmailTemplateRenderer.Validate(definition,
            new EmailTemplateContent("Hi\nthere", "", "<a href=\"{{VerifyUrl}}\">x</a> {{Full Name}}"));

        Assert.Contains(issues, issue => issue.Field == "subject" && issue.Code == "LINE_BREAK");
        Assert.Contains(issues, issue => issue.Field == "bodyHtml" && issue.Code == "MALFORMED_VARIABLE");
    }

    [Fact]
    public void Validate_DoesNotMistakeProseForAScriptLink()
    {
        var definition = EmailTemplateCatalog.Get(EmailTemplateCatalog.AuthVerifyEmail);

        var issues = EmailTemplateRenderer.Validate(definition,
            new EmailTemplateContent("S", "", "<p>Your metadata: stays private.</p><a href=\"{{VerifyUrl}}\">x</a>"));

        Assert.Empty(issues);
    }

    [Fact]
    public async Task Composer_IgnoresAStoredTemplateThatNoLongerValidates()
    {
        var composer = new EmailTemplateComposer(new FixedSource(new StoredEmailTemplate("Broken", "", "<p>{{Gone}}</p>", 4)));

        var email = await composer.ComposeAsync(EmailTemplateCatalog.AuthVerifyEmail, new Dictionary<string, string>
        {
            ["FullName"] = "Linh",
            ["VerifyUrl"] = "https://x.test/verify",
        });

        Assert.Equal("Verify your WarpTalk email", email.Subject);
    }

    private sealed class FixedSource(StoredEmailTemplate template) : IEmailTemplateSource
    {
        public Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, CancellationToken ct = default) =>
            Task.FromResult<StoredEmailTemplate?>(template);
    }
}
