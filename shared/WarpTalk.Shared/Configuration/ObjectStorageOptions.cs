using Microsoft.Extensions.Configuration;

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

    public static ObjectStorageOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var s3Section = section.GetSection(nameof(S3));

        return new ObjectStorageOptions
        {
            Provider = section[nameof(Provider)] ?? StorageProviders.Local,
            MasterKey = section[nameof(MasterKey)],
            S3 = new S3ObjectStorageOptions
            {
                ServiceUrl = s3Section[nameof(S3ObjectStorageOptions.ServiceUrl)],
                AccessKey = s3Section[nameof(S3ObjectStorageOptions.AccessKey)],
                SecretKey = s3Section[nameof(S3ObjectStorageOptions.SecretKey)],
                BucketName = s3Section[nameof(S3ObjectStorageOptions.BucketName)],
                EnsureBucketExists = bool.TryParse(
                    s3Section[nameof(S3ObjectStorageOptions.EnsureBucketExists)],
                    out var ensureBucketExists) && ensureBucketExists
            }
        };
    }
}
