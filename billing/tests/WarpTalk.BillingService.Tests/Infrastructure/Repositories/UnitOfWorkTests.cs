using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.BillingService.Tests.Infrastructure.Repositories;

public class UnitOfWorkTests
{
    [Fact]
    public async Task SaveChangesAsync_ShouldCallDbContextSaveChangesAsync()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .Options;

        var dbContextMock = new Mock<BillingDbContext>(options);

        dbContextMock
            .Setup(db => db.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var unitOfWork = new UnitOfWork(dbContextMock.Object);

        // Act
        var result = await unitOfWork.SaveChangesAsync();

        // Assert
        result.Should().Be(1);
        dbContextMock.Verify(db => db.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Repositories_ShouldBeInitializedOnFirstAccess()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .Options;

        var dbContextMock = new Mock<BillingDbContext>(options);
        var unitOfWork = new UnitOfWork(dbContextMock.Object);

        // Act
        var Plans = unitOfWork.Plans;
        var Plans2 = unitOfWork.Plans;

        // Assert
        Plans.Should().NotBeNull();
        Plans.Should().BeSameAs(Plans2); // Singleton per UnitOfWork instance
    }
}
