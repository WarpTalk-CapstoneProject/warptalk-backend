using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.MeetingService.Infrastructure.Storage;
using WarpTalk.Shared.Configuration;

namespace WarpTalk.MeetingService.Tests.Services;

public sealed class S3MeetingChatFileStorageTests
{
    private readonly Mock<IAmazonS3> _s3 = new();
    private readonly S3MeetingChatFileStorage _storage;

    public S3MeetingChatFileStorageTests()
    {
        _storage = new S3MeetingChatFileStorage(
            _s3.Object,
            Options.Create(new ObjectStorageOptions
            {
                Provider = StorageProviders.MinIO,
                S3 = new S3ObjectStorageOptions
                {
                    BucketName = "warptalk-meeting-chat"
                }
            }));
    }

    [Fact]
    public async Task SaveAsync_WritesToConfiguredBucketAndKey()
    {
        PutObjectRequest? captured = null;
        _s3.Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("meeting attachment"));

        await _storage.SaveAsync("rooms/room-id/file-id.txt", content);

        Assert.NotNull(captured);
        Assert.Equal("warptalk-meeting-chat", captured.BucketName);
        Assert.Equal("rooms/room-id/file-id.txt", captured.Key);
        Assert.Same(content, captured.InputStream);
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsIndependentReadableStream()
    {
        var remoteContent = new MemoryStream(Encoding.UTF8.GetBytes("stored attachment"));
        _s3.Setup(client => client.GetObjectAsync(
                It.IsAny<GetObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse { ResponseStream = remoteContent });

        await using var result = await _storage.OpenReadAsync("rooms/room-id/file-id.txt");
        Assert.Equal(0, result.Position);
        using var reader = new StreamReader(result);

        Assert.Equal("stored attachment", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task DeleteAsync_DeletesConfiguredObject()
    {
        DeleteObjectRequest? captured = null;
        _s3.Setup(client => client.DeleteObjectAsync(
                It.IsAny<DeleteObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<DeleteObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.NoContent });

        await _storage.DeleteAsync("rooms/room-id/file-id.txt");

        Assert.NotNull(captured);
        Assert.Equal("warptalk-meeting-chat", captured.BucketName);
        Assert.Equal("rooms/room-id/file-id.txt", captured.Key);
    }
}
