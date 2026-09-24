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
}
