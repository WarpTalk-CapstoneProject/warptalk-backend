namespace WarpTalk.Shared.Configuration;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = StorageProviders.Local;

    public string? MasterKey { get; set; }

    public S3ObjectStorageOptions S3 { get; set; } = new();

    public bool UsesS3CompatibleProvider =>
        string.Equals(Provider, StorageProviders.S3, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Provider, StorageProviders.MinIO, StringComparison.OrdinalIgnoreCase);
}

