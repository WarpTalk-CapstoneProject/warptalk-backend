using System.Linq.Expressions;
using NSubstitute;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// A repository substitute over a list: queries compile their predicate and run it, writes change
/// the list. So a wrong filter in the code under test returns wrong rows here too.
/// </summary>
internal static class InMemoryRepository
{
    public static TRepository Create<TRepository, T>(List<T> store, Func<T, Guid> id)
        where TRepository : class, IGenericRepository<T>
        where T : class
    {
        var repository = Substitute.For<TRepository>();
        repository.FindAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<T>)store.Where(call.Arg<Expression<Func<T, bool>>>().Compile()).ToList());
        repository.FirstOrDefaultAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => store.FirstOrDefault(call.Arg<Expression<Func<T, bool>>>().Compile()));
        repository.AnyAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => store.Any(call.Arg<Expression<Func<T, bool>>>().Compile()));
        repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => store.FirstOrDefault(item => id(item) == call.Arg<Guid>()));
        repository.AddAsync(Arg.Any<T>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                store.Add(call.Arg<T>());
                return Task.CompletedTask;
            });
        repository.When(r => r.Remove(Arg.Any<T>())).Do(call => store.Remove(call.Arg<T>()));
        return repository;
    }
}
