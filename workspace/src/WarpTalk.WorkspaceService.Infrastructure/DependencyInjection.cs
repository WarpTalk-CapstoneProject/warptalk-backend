using System;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Infrastructure.Storage;

namespace WarpTalk.WorkspaceService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddWarpTalkObjectStorageOptions(configuration);

        var storageOptions = ObjectStorageOptions.FromConfiguration(configuration);
        if (storageOptions.UsesS3CompatibleProvider)
        {
            services.AddSingleton<IAmazonS3>(sp =>
            {
                var serviceUrl = storageOptions.S3.ServiceUrl ?? WorkspaceDocumentConstants.StorageEncryption.DefaultS3ServiceUrl;
                var s3Config = new AmazonS3Config
                {
                    ServiceURL = serviceUrl,
                    ForcePathStyle = true,
                    UseHttp = !serviceUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase)
                };
                var accessKey = storageOptions.S3.AccessKey ?? WorkspaceDocumentConstants.StorageEncryption.DefaultS3AccessKey;
                var secretKey = storageOptions.S3.SecretKey ?? WorkspaceDocumentConstants.StorageEncryption.DefaultS3SecretKey;
                return new AmazonS3Client(accessKey, secretKey, s3Config);
            });
            services.AddSingleton<IWorkspaceDocumentStorage, S3EncryptedWorkspaceDocumentStorage>();
        }
        else
        {
            services.AddSingleton<IWorkspaceDocumentStorage, LocalEncryptedWorkspaceDocumentStorage>();
        }

        services.AddScoped<IWorkspaceInvitationEmailComposer, Services.WorkspaceInvitationEmailComposer>();
        services.AddScoped<IDocumentTextChunker, Services.DocumentTextChunker>();
        services.AddScoped<IAiPolicyResolver, Services.AiPolicyResolver>();
        services.AddScoped<IEmbeddingIndexPublisher, Services.RedisEmbeddingIndexPublisher>();
        services.AddScoped<IDocumentEmbeddingResultProcessor, Services.DocumentEmbeddingResultProcessor>();
        services.AddScoped<IWorkspaceDocumentEventPublisher, Clients.RedisDocumentEventPublisher>();
        services.AddScoped<IWorkspaceEventPublisher, Clients.HybridWorkspaceEventPublisher>();

        return services;
    }
}
