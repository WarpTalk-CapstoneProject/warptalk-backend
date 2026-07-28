namespace WarpTalk.BillingService.Tests.Contracts;

public sealed class RateCardIdentityContractTests
{
    [Fact]
    public void AdminRateCardUpsert_MustNotCreateUnregisteredBillingIdentity()
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
