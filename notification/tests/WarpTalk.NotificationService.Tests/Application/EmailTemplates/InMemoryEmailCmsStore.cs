using Moq;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.NotificationService.Tests.Application.EmailTemplates;

/// <summary>
/// The email CMS tables as lists behind a mocked unit of work — enough to publish through the
/// admin services and read the result back through the very repository methods a send uses.
/// </summary>
internal sealed class InMemoryEmailCmsStore
{
    public List<EmailContentVariant> Variants { get; } = [];
    public List<EmailBlock> Blocks { get; } = [];
    public List<EmailCmsVersion> Versions { get; } = [];
    public List<EmailSampleDataSet> SampleSets { get; } = [];
    public List<EmailDeliveryStat> Stats { get; } = [];
    public List<EmailCustomTemplate> Custom { get; } = [];
    public List<EmailCampaign> Campaigns { get; } = [];
    public List<EmailCampaignRecipient> Recipients { get; } = [];
    /// <summary>(user, notification type) pairs that turned email off.</summary>
    public HashSet<(Guid UserId, string Type)> OptOuts { get; } = [];
    public int SaveCount { get; private set; }

    public Mock<IUnitOfWork> UnitOfWork { get; } = new();

    public static readonly AdminActorContext Admin = new(Guid.NewGuid(), "test-correlation");

    public InMemoryEmailCmsStore()
    {
        UnitOfWork.Setup(u => u.EmailContentVariantRepository).Returns(new VariantRepository(this));
        UnitOfWork.Setup(u => u.EmailBlockRepository).Returns(new BlockRepository(this));
        UnitOfWork.Setup(u => u.EmailCmsVersionRepository).Returns(new VersionRepository(this));
        UnitOfWork.Setup(u => u.EmailSampleDataSetRepository).Returns(new SampleRepository(this));
        UnitOfWork.Setup(u => u.EmailDeliveryStatRepository).Returns(new StatRepository(this));
        UnitOfWork.Setup(u => u.EmailCustomTemplateRepository).Returns(new CustomRepository(this));
        UnitOfWork.Setup(u => u.EmailCampaignRepository).Returns(new CampaignRepository(this));
        UnitOfWork.Setup(u => u.EmailCampaignRecipientRepository).Returns(new RecipientRepository(this));
        var preferences = new Mock<INotificationPreferenceRepository>();
        preferences
            .Setup(p => p.ListEmailOptOutsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, string type, CancellationToken _) =>
                (IReadOnlySet<Guid>)ids.Where(id => OptOuts.Contains((id, type))).ToHashSet());
        UnitOfWork.Setup(u => u.NotificationPreferenceRepository).Returns(preferences.Object);
        UnitOfWork.Setup(u => u.SaveChangesAsync()).ReturnsAsync(() => ++SaveCount);

    }

    private sealed class VariantRepository(InMemoryEmailCmsStore store) : IEmailContentVariantRepository
    {
        public Task AddAsync(EmailContentVariant variant, CancellationToken ct = default) { store.Variants.Add(variant); return Task.CompletedTask; }
        public void Remove(EmailContentVariant variant) => store.Variants.Remove(variant);
        public Task<EmailContentVariant?> GetAsync(string templateKey, string locale, CancellationToken ct = default) =>
            Task.FromResult(store.Variants.FirstOrDefault(v => v.TemplateKey == templateKey && v.Locale == locale));
        public Task<IReadOnlyList<EmailContentVariant>> ListAsync(string? templateKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailContentVariant>>(store.Variants.Where(v => templateKey == null || v.TemplateKey == templateKey).ToList());
        public Task<EmailContentVariant?> GetPublishedAsync(string templateKey, string locale, CancellationToken ct = default) =>
            Task.FromResult(store.Variants.FirstOrDefault(v => v.TemplateKey == templateKey && v.Locale == locale
                && v.Status == EmailCmsConstants.StatusActive && v.PublishedVersion > 0 && v.PublishedBodyHtml != null));
        public Task<IReadOnlyList<EmailContentVariant>> ListUsingLayoutAsync(Guid layoutId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailContentVariant>>(store.Variants.Where(v => v.DraftLayoutId == layoutId || v.PublishedLayoutId == layoutId).ToList());
    }

    private sealed class BlockRepository(InMemoryEmailCmsStore store) : IEmailBlockRepository
    {
        public Task AddAsync(EmailBlock block, CancellationToken ct = default) { store.Blocks.Add(block); return Task.CompletedTask; }
        public void Remove(EmailBlock block) => store.Blocks.Remove(block);
        public Task<EmailBlock?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(store.Blocks.FirstOrDefault(b => b.Id == id));
        public Task<IReadOnlyList<EmailBlock>> ListAsync(string? kind, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailBlock>>(store.Blocks.Where(b => kind == null || b.Kind == kind).ToList());
        public Task<bool> KeyExistsAsync(string kind, string key, Guid? exceptId, CancellationToken ct = default) =>
            Task.FromResult(store.Blocks.Any(b => b.Kind == kind && b.Key == key && b.Id != exceptId));
        public Task<IReadOnlyList<EmailBlock>> GetDefaultLayoutsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailBlock>>(store.Blocks.Where(b => b.Kind == EmailCmsConstants.KindLayout && b.IsDefault).ToList());
        public Task<IReadOnlyDictionary<string, string>> GetPublishedPartialsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(store.Blocks
                .Where(b => b.Kind == EmailCmsConstants.KindPartial && b.Status == EmailCmsConstants.StatusActive && b.PublishedHtml != null)
                .ToDictionary(b => b.Key, b => b.PublishedHtml!));
    }

    private sealed class VersionRepository(InMemoryEmailCmsStore store) : IEmailCmsVersionRepository
    {
        public Task AddAsync(EmailCmsVersion version, CancellationToken ct = default) { store.Versions.Add(version); return Task.CompletedTask; }
        public Task<IReadOnlyList<EmailCmsVersion>> ListAsync(string ownerType, Guid ownerId, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailCmsVersion>>(store.Versions.Where(v => v.OwnerType == ownerType && v.OwnerId == ownerId)
                .OrderByDescending(v => v.Version).Take(take).ToList());
        public Task<EmailCmsVersion?> GetAsync(string ownerType, Guid ownerId, int version, CancellationToken ct = default) =>
            Task.FromResult(store.Versions.FirstOrDefault(v => v.OwnerType == ownerType && v.OwnerId == ownerId && v.Version == version));
    }

    private sealed class SampleRepository(InMemoryEmailCmsStore store) : IEmailSampleDataSetRepository
    {
        public Task AddAsync(EmailSampleDataSet set, CancellationToken ct = default) { store.SampleSets.Add(set); return Task.CompletedTask; }
        public void Remove(EmailSampleDataSet set) => store.SampleSets.Remove(set);
        public Task<EmailSampleDataSet?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(store.SampleSets.FirstOrDefault(s => s.Id == id));
        public Task<IReadOnlyList<EmailSampleDataSet>> ListAsync(string templateKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailSampleDataSet>>(store.SampleSets.Where(s => s.TemplateKey == templateKey).ToList());
    }

    private sealed class StatRepository(InMemoryEmailCmsStore store) : IEmailDeliveryStatRepository
    {
        public Task IncrementAsync(string templateKey, string locale, DateOnly day, bool succeeded, CancellationToken ct = default)
        {
            var row = store.Stats.FirstOrDefault(s => s.TemplateKey == templateKey && s.Locale == locale && s.Day == day);
            if (row is null)
            {
                row = new EmailDeliveryStat { TemplateKey = templateKey, Locale = locale, Day = day };
                store.Stats.Add(row);
            }
            if (succeeded) row.SentCount++; else row.FailedCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EmailDeliveryStat>> ListSinceAsync(string? templateKey, DateOnly since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailDeliveryStat>>(store.Stats.Where(s => s.Day >= since && (templateKey == null || s.TemplateKey == templateKey)).ToList());
    }

    private sealed class CustomRepository(InMemoryEmailCmsStore store) : IEmailCustomTemplateRepository
    {
        public Task AddAsync(EmailCustomTemplate template, CancellationToken ct = default) { store.Custom.Add(template); return Task.CompletedTask; }
        public void Remove(EmailCustomTemplate template) => store.Custom.Remove(template);
        public Task<EmailCustomTemplate?> GetByKeyAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(store.Custom.FirstOrDefault(t => t.Key == key));
        public Task<IReadOnlyList<EmailCustomTemplate>> ListAsync(bool includeDeleted, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailCustomTemplate>>(store.Custom
                .Where(t => includeDeleted || t.Status == EmailCmsConstants.StatusActive).OrderBy(t => t.Name).ToList());
    }

    private sealed class CampaignRepository(InMemoryEmailCmsStore store) : IEmailCampaignRepository
    {
        public Task AddAsync(EmailCampaign campaign, CancellationToken ct = default) { store.Campaigns.Add(campaign); return Task.CompletedTask; }
        public Task<EmailCampaign?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(store.Campaigns.FirstOrDefault(c => c.Id == id));
        public Task<IReadOnlyList<EmailCampaign>> ListForTemplateAsync(string templateKey, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailCampaign>>(store.Campaigns.Where(c => c.TemplateKey == templateKey).OrderByDescending(c => c.CreatedAt).Take(limit).ToList());
        public Task<int> CountCreatedSinceAsync(Guid createdBy, DateTime since, CancellationToken ct = default) =>
            Task.FromResult(store.Campaigns.Count(c => c.CreatedBy == createdBy && c.Source == EmailCmsConstants.CampaignSourceManual && c.CreatedAt >= since));
        public Task<bool> AnySentAsync(string templateKey, CancellationToken ct = default) =>
            Task.FromResult(store.Campaigns.Any(c => c.TemplateKey == templateKey && c.SentCount > 0));
        public Task<EmailCampaign?> NextDueAsync(DateTime now, CancellationToken ct = default) =>
            Task.FromResult(store.Campaigns.Where(c => c.Status == EmailCmsConstants.CampaignSending).OrderBy(c => c.StartedAt).FirstOrDefault()
                ?? store.Campaigns.Where(c => c.Status == EmailCmsConstants.CampaignQueued && c.ScheduledAt <= now).OrderBy(c => c.ScheduledAt).FirstOrDefault());
    }

    private sealed class RecipientRepository(InMemoryEmailCmsStore store) : IEmailCampaignRecipientRepository
    {
        public Task AddRangeAsync(IEnumerable<EmailCampaignRecipient> recipients, CancellationToken ct = default) { store.Recipients.AddRange(recipients); return Task.CompletedTask; }
        public Task<IReadOnlyList<EmailCampaignRecipient>> NextPendingAsync(Guid campaignId, int batchSize, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmailCampaignRecipient>>(store.Recipients
                .Where(r => r.CampaignId == campaignId && r.Status == EmailCmsConstants.RecipientPending).OrderBy(r => r.Id).Take(batchSize).ToList());
        public Task<bool> AnyAsync(Guid campaignId, CancellationToken ct = default) => Task.FromResult(store.Recipients.Any(r => r.CampaignId == campaignId));
        public Task<(IReadOnlyList<EmailCampaignRecipient> Items, int Total)> PageAsync(Guid campaignId, string? status, int page, int pageSize, CancellationToken ct = default)
        {
            var all = store.Recipients.Where(r => r.CampaignId == campaignId && (status == null || r.Status == status)).OrderBy(r => r.Email).ToList();
            return Task.FromResult<(IReadOnlyList<EmailCampaignRecipient>, int)>((all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), all.Count));
        }
        public Task<int> SkipPendingAsync(Guid campaignId, string reason, CancellationToken ct = default)
        {
            var pending = store.Recipients.Where(r => r.CampaignId == campaignId && r.Status == EmailCmsConstants.RecipientPending).ToList();
            foreach (var r in pending) { r.Status = EmailCmsConstants.RecipientSkipped; r.Error = reason; }
            return Task.FromResult(pending.Count);
        }
    }
}
