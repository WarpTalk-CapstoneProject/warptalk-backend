using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Tests.Application.EmailTemplates;

public sealed class AdminEmailTemplateServiceTests
{
    private static readonly Guid AdminId = Guid.NewGuid();
    private const string Key = EmailTemplateCatalog.AuthPasswordReset;

    private readonly InMemoryTemplateStore _store = new();
    private readonly Mock<IEmailSender> _sender = new();

    private AdminEmailTemplateService Service() =>
        new(_store.UnitOfWork.Object, NullLogger<AdminEmailTemplateService>.Instance, _sender.Object);

    private static SaveEmailTemplateRequest Valid(string subject = "Reset it", int? expectedVersion = null) =>
        new(subject, "New password", "<a href=\"{{ResetUrl}}\">Reset</a>", expectedVersion);

    [Fact]
    public async Task List_ShowsEveryEmailTheCatalogKnows_CustomisedOrNot()
    {
        await Service().SaveAsync(AdminId, Key, Valid());

        var list = (await Service().ListAsync()).Value!;

        Assert.Equal(EmailTemplateCatalog.All.Select(d => d.Key), list.Select(item => item.Key));
        var reset = list.Single(item => item.Key == Key);
        Assert.True(reset.IsCustomized);
        Assert.Equal(1, reset.Version);
        Assert.Equal("Reset it", reset.Subject);
        var verify = list.Single(item => item.Key == EmailTemplateCatalog.AuthVerifyEmail);
        Assert.False(verify.IsCustomized);
        Assert.Equal("Verify your WarpTalk email", verify.Subject);
    }

    [Fact]
    public async Task Save_WritesTheRowAndAHistoryEntry_AndCountsVersions()
    {
        var service = Service();
        await service.SaveAsync(AdminId, Key, Valid("First"));
        var second = await service.SaveAsync(AdminId, Key, Valid("Second", expectedVersion: 1) with { Note = "tone" });

        Assert.True(second.IsSuccess, second.Error);
        var row = Assert.Single(_store.Templates);
        Assert.Equal(EmailTemplateConstants.ChannelEmail, row.Channel);
        Assert.Equal("Second", row.Subject);
        Assert.Equal(2, row.Version);
        Assert.True(row.IsActive);
        Assert.Equal(AdminId, row.UpdatedBy);
        Assert.Equal(new[] { 1, 2 }, _store.Versions.Select(v => v.Version));
        Assert.All(_store.Versions, v => Assert.Equal(EmailTemplateConstants.ActionSaved, v.Action));
        Assert.Equal("tone", _store.Versions[1].Note);
    }

    [Fact]
    public async Task Save_OverSomeoneElsesNewerVersion_IsAConflict()
    {
        var service = Service();
        await service.SaveAsync(AdminId, Key, Valid("First"));

        var stale = await service.SaveAsync(AdminId, Key, Valid("Mine", expectedVersion: 0));

        Assert.Equal(ErrorCodes.Conflict, stale.ErrorCode);
        Assert.Equal("First", _store.Templates.Single().Subject);
    }

    [Theory]
    [InlineData("<a href=\"{{ResetUrl}}\">Reset</a> {{Nope}}", "is not a variable")]
    [InlineData("<p>No link at all</p>", "{{ResetUrl}} must appear")]
    [InlineData("<a href=\"{{ResetUrl}}\">x</a><script>alert(1)</script>", "<script> is not allowed")]
    [InlineData("<a href=\"{{ResetUrl}}\" onclick=\"x()\">x</a>", "Event handler")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a> {{ResetUrl}}", "javascript: links")]
    public async Task Save_RefusesATemplateThatWouldSendABrokenOrUnsafeEmail(string body, string expected)
    {
        var result = await Service().SaveAsync(AdminId, Key, new SaveEmailTemplateRequest("Subject", "Heading", body));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains(expected, result.Error);
        Assert.Empty(_store.Templates);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task Save_ForAnEmailThePlatformDoesNotSend_IsNotFound()
    {
        var result = await Service().SaveAsync(AdminId, "billing.invoice", Valid());

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Reset_DeactivatesTheRow_RecordsIt_AndCurrentIsTheDefaultAgain()
    {
        var service = Service();
        await service.SaveAsync(AdminId, Key, Valid("Custom"));

        var reset = await service.ResetAsync(AdminId, Key);

        Assert.True(reset.IsSuccess, reset.Error);
        Assert.False(reset.Value!.Template.IsCustomized);
        Assert.Equal("Reset your WarpTalk password", reset.Value.Current.Subject);
        Assert.False(_store.Templates.Single().IsActive);
        Assert.Equal(EmailTemplateConstants.ActionReset, _store.Versions.Last().Action);
        Assert.Equal(2, _store.Versions.Last().Version);
    }

    [Fact]
    public async Task Reset_OfAnUneditedEmail_RecordsNothing()
    {
        var reset = await Service().ResetAsync(AdminId, Key);

        Assert.True(reset.IsSuccess);
        Assert.Empty(_store.Versions);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task Restore_BringsBackAnOldVersionAsANewOne()
    {
        var service = Service();
        await service.SaveAsync(AdminId, Key, Valid("One"));
        await service.SaveAsync(AdminId, Key, Valid("Two"));

        var restored = await service.RestoreAsync(AdminId, Key, 1);

        Assert.True(restored.IsSuccess, restored.Error);
        Assert.Equal("One", restored.Value!.Current.Subject);
        Assert.Equal(3, restored.Value.Template.Version);
        var entry = _store.Versions.Last();
        Assert.Equal(EmailTemplateConstants.ActionRestored, entry.Action);
        Assert.Equal(1, entry.RestoredFromVersion);

        var versions = (await service.ListVersionsAsync(Key)).Value!;
        Assert.Equal(new[] { 3, 2, 1 }, versions.Select(v => v.Version));
    }

    [Fact]
    public async Task Restore_OfAVersionThatDoesNotExist_IsNotFound()
    {
        var result = await Service().RestoreAsync(AdminId, Key, 9);

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public void Preview_RendersWithSampleValues_AndListsProblemsWithoutRefusing()
    {
        var preview = Service().Preview(Key, new EmailTemplateDraftRequest(
            "Hi {{FullName}}", "Heading", "<p>{{Typo}}</p>"));

        Assert.True(preview.IsSuccess);
        Assert.Equal("Hi Linh Nguyen", preview.Value!.Subject);
        Assert.Contains("{{Typo}}", preview.Value.Html);
        Assert.Contains(preview.Value.Issues, issue => issue.Code == "UNKNOWN_VARIABLE");
        Assert.Contains(preview.Value.Issues, issue => issue.Code == "MISSING_REQUIRED_VARIABLE");
    }

    [Fact]
    public async Task SendTest_GoesToTheAdminOnly_FilledWithSamples()
    {
        EmailMessage? sent = null;
        _sender.Setup(s => s.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((message, _) => sent = message)
            .ReturnsAsync(true);

        var result = await Service().SendTestAsync(Key, new EmailTemplateDraftRequest(
            "Reset for {{FullName}}", "Heading", "<a href=\"{{ResetUrl}}\">Reset</a>"), "admin@warptalk.vn");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("admin@warptalk.vn", sent!.ToEmail);
        Assert.Equal("[Test] Reset for Linh Nguyen", sent.Subject);
        Assert.Contains("https://app.warptalk.vn/reset-password?token=sample-token", sent.HtmlBody);
        Assert.Empty(_store.Templates);
    }

    [Fact]
    public async Task SendTest_OfAnInvalidDraft_SendsNothing()
    {
        var result = await Service().SendTestAsync(Key, new EmailTemplateDraftRequest("S", "H", "<p>no link</p>"), "admin@warptalk.vn");

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        _sender.Verify(s => s.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendTest_WhenTheProviderRefuses_SaysSo()
    {
        _sender.Setup(s => s.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await Service().SendTestAsync(Key, new EmailTemplateDraftRequest("S", "H", "<a href=\"{{ResetUrl}}\">x</a>"), "admin@warptalk.vn");

        Assert.Equal(ErrorCodes.ServiceUnavailable, result.ErrorCode);
    }
}
