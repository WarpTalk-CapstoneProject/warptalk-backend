using System.Reflection;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Infrastructure.Services;

namespace WarpTalk.BillingService.Tests.Contracts;

public sealed class RateCardIdentityContractTests
{
    [Fact]
    public void AdminRateCardUpsert_MustRejectUnregisteredBillingIdentity()
    {
        var request = new UpsertUsageRateCardRequest(
            "BOGUS_TEST_NOT_SEEDED",
            "unit",
            "test",
            "test-model",
            null,
            null,
            1m,
            "VND",
            0.1m,
            2m,
            true);

        Assert.False(IsRegisteredBillingIdentity(request));
    }

    [Fact]
    public void AdminRateCardUpsert_AllowsSeededBillingIdentity()
    {
        var request = new UpsertUsageRateCardRequest(
            "STT",
            "second",
            "openai",
            "gpt-4o-transcribe",
            null,
            null,
            1.643750m,
            "VND",
            0.0001000000m,
            2.5m,
            true);

        Assert.True(IsRegisteredBillingIdentity(request));
    }

    [Fact]
    public void AdminRateCardUpsert_SourceDocumentsMigrationOnlyIdentityChanges()
    {
        var root = FindBackendRoot();
        var servicePath = Path.Combine(
            root,
            "billing/src/WarpTalk.BillingService.Infrastructure/Services/UsageRateCardAdminService.cs");

        var source = File.ReadAllText(servicePath);

        Assert.Contains("RateCardIdentityExistsAsync", source, StringComparison.Ordinal);
        Assert.Contains("Usage rate-card identity is not registered", source, StringComparison.Ordinal);
        Assert.Contains("Add new billing identities through a migration/backend release first", source, StringComparison.Ordinal);
    }

    private static bool IsRegisteredBillingIdentity(UpsertUsageRateCardRequest request)
    {
        var method = typeof(UsageRateCardAdminService).GetMethod(
            "IsRegisteredBillingIdentity",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (bool)method.Invoke(null, new object[] { request })!;
    }

    private static string FindBackendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate backend repository root.");
    }
}
