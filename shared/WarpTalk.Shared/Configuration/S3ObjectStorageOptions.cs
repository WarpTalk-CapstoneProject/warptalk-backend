namespace WarpTalk.Shared.Configuration;

public sealed class S3ObjectStorageOptions
{
    public string? ServiceUrl { get; set; }

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public string? BucketName { get; set; }

    public bool EnsureBucketExists { get; set; }
}
