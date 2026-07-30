namespace WarpTalk.BillingService.Tests.Contracts;

public sealed class OutboxRetentionContractTests
{
    [Fact]
    public void BillingAndWorkspaceOutboxes_HaveHotPartialIndexesAndRetentionWorkers()
    {
        var root = FindBackendRoot();
        var billingMigrationPath = Path.Combine(
            root,
            "billing/database/migrations/003-add-outbox-retention.sql");
        var workspaceMigrationPath = Path.Combine(
            root,
            "workspace/database/migrations/20260728170000_add_outbox_retention.sql");
        Assert.True(File.Exists(billingMigrationPath));
        Assert.True(File.Exists(workspaceMigrationPath));

        var billingMigration = File.ReadAllText(billingMigrationPath);
        var workspaceMigration = File.ReadAllText(workspaceMigrationPath);
        var billingWorker = File.ReadAllText(Path.Combine(
            root,
            "billing/src/WarpTalk.BillingService.API/Workers/BillingOutboxWorker.cs"));
        var workspaceWorker = File.ReadAllText(Path.Combine(
            root,
            "workspace/src/WarpTalk.WorkspaceService.Infrastructure/BackgroundServices/WorkspaceOutboxWorker.cs"));

        Assert.Contains(
            "WHERE published_at IS NULL AND dead_lettered_at IS NULL",
            billingMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE published_at IS NULL AND dead_lettered_at IS NULL",
            workspaceMigration,
            StringComparison.Ordinal);
        Assert.Contains("PurgePublishedBeforeAsync", billingWorker, StringComparison.Ordinal);
        Assert.Contains("PurgePublishedBeforeAsync", workspaceWorker, StringComparison.Ordinal);
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
