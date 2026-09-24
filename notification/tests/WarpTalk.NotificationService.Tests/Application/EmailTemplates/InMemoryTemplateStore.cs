using System.Linq.Expressions;
using Moq;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;

namespace WarpTalk.NotificationService.Tests.Application.EmailTemplates;

/// <summary>
/// notification_templates and notification_template_versions as lists, behind a mocked unit of
/// work — enough to run a save through the admin service and read it back through the same
/// repository methods the senders use.
/// </summary>
internal sealed class InMemoryTemplateStore
{
    public List<NotificationTemplate> Templates { get; } = [];
    public List<NotificationTemplateVersion> Versions { get; } = [];
    public int SaveCount { get; private set; }

    public Mock<IUnitOfWork> UnitOfWork { get; } = new();

    public InMemoryTemplateStore()
    {
        UnitOfWork.Setup(u => u.NotificationTemplateRepository).Returns(new TemplateRepository(this));
        UnitOfWork.Setup(u => u.NotificationTemplateVersionRepository).Returns(new VersionRepository(this));
        UnitOfWork.Setup(u => u.SaveChangesAsync()).ReturnsAsync(() => ++SaveCount);
    }

    private sealed class TemplateRepository(InMemoryTemplateStore store) : INotificationTemplateRepository
    {
        public Task<NotificationTemplate?> GetByTypeAsync(string type, string channel, CancellationToken ct = default) =>
            Task.FromResult(store.Templates.FirstOrDefault(t => t.Type == type && t.Channel == channel));

        public Task<NotificationTemplate?> GetActiveByTypeAsync(string type, string channel, CancellationToken ct = default) =>
            Task.FromResult(store.Templates.FirstOrDefault(t => t.Type == type && t.Channel == channel && t.IsActive));

        public Task<IReadOnlyList<NotificationTemplate>> ListByChannelAsync(string channel, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationTemplate>>(store.Templates.Where(t => t.Channel == channel).ToList());

        public Task AddAsync(NotificationTemplate entity)
        {
            store.Templates.Add(entity);
            return Task.CompletedTask;
        }

        public Task<NotificationTemplate?> GetByIdAsync(Guid id) => Task.FromResult(store.Templates.FirstOrDefault(t => t.Id == id));
        public Task<IEnumerable<NotificationTemplate>> GetAllAsync() => Task.FromResult<IEnumerable<NotificationTemplate>>(store.Templates);
        public Task<IEnumerable<NotificationTemplate>> FindAsync(Expression<Func<NotificationTemplate, bool>> predicate) =>
            Task.FromResult<IEnumerable<NotificationTemplate>>(store.Templates.Where(predicate.Compile()).ToList());
        public Task AddRangeAsync(IEnumerable<NotificationTemplate> entities)
        {
            store.Templates.AddRange(entities);
            return Task.CompletedTask;
        }
        public void Update(NotificationTemplate entity) { }
        public void Remove(NotificationTemplate entity) => store.Templates.Remove(entity);
        public IQueryable<NotificationTemplate> Query() => store.Templates.AsQueryable();
        public Task<int> CountAsync(Expression<Func<NotificationTemplate, bool>> predicate) =>
            Task.FromResult(store.Templates.Count(predicate.Compile()));
        public Task<IEnumerable<NotificationTemplate>> FindWithPaginationAsync(
            Expression<Func<NotificationTemplate, bool>> predicate, int skip, int take,
            Func<IQueryable<NotificationTemplate>, IOrderedQueryable<NotificationTemplate>>? orderBy = null) =>
            Task.FromResult<IEnumerable<NotificationTemplate>>(store.Templates.Where(predicate.Compile()).Skip(skip).Take(take).ToList());
    }

    private sealed class VersionRepository(InMemoryTemplateStore store) : INotificationTemplateVersionRepository
    {
        public Task AddAsync(NotificationTemplateVersion version, CancellationToken ct = default)
        {
            store.Versions.Add(version);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationTemplateVersion>> ListAsync(string type, string channel, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationTemplateVersion>>(store.Versions
                .Where(v => v.TemplateType == type && v.Channel == channel)
                .OrderByDescending(v => v.Version)
                .Take(take)
                .ToList());

        public Task<NotificationTemplateVersion?> GetAsync(string type, string channel, int version, CancellationToken ct = default) =>
            Task.FromResult(store.Versions.FirstOrDefault(v => v.TemplateType == type && v.Channel == channel && v.Version == version));
    }
}
