using System;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;
using WarpTalk.WorkspaceService.Infrastructure.Caching;
using WarpTalk.WorkspaceService.Infrastructure.Clients;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using WarpTalk.WorkspaceService.Infrastructure.Repositories;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using WarpTalk.WorkspaceService.Infrastructure.Storage;
using WarpTalk.WorkspaceService.Application.Services;

namespace WarpTalk.WorkspaceService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        // 1. Database Context
        var connectionString = configuration.GetConnectionString("WorkspaceDb") 
                              ?? "Host=localhost;Database=warptalk;Username=postgres;Password=postgres;Search Path=workspace,public";
        services.AddDbContext<WorkspaceDbContext>(options =>
            options.UseNpgsql(connectionString));

        // 2. Repositories & Unit of Work
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IWorkspaceMemberRepository, WorkspaceMemberRepository>();
        services.AddScoped<IWorkspaceInvitationRepository, WorkspaceInvitationRepository>();

        // 3. Object Storage Options & Adapters
        services.AddWarpTalkObjectStorageOptions(configuration);

        var storageOptions = configuration.GetSection(ObjectStorageOptions.SectionName).Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
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

        // 4. Infrastructure Services & Adapters
        services.AddScoped<IWorkspaceInvitationEmailComposer, WorkspaceInvitationEmailComposer>();
        services.AddScoped<IDocumentTextExtractor, DocumentTextExtractor>();
        services.AddScoped<IDocumentSecurityScanner, DocumentSecurityScanner>();
        services.AddScoped<IDocumentTextChunker, DocumentTextChunker>();
        services.AddScoped<IAiPolicyResolver, AiPolicyResolver>();
        services.AddScoped<IEmbeddingIndexPublisher, RedisEmbeddingIndexPublisher>();
        services.AddScoped<IDocumentEmbeddingResultProcessor, DocumentEmbeddingResultProcessor>();
        services.AddScoped<IWorkspaceDocumentEventPublisher, RedisDocumentEventPublisher>();
        services.AddScoped<IWorkspaceEventPublisher, HybridWorkspaceEventPublisher>();

        // 5. Distributed Redis Cache & Multiplexer
        var redisConnectionString = configuration["Redis:ConnectionString"] ?? "localhost:6379";
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
        });
        services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));
        services.AddScoped<IWorkspaceCacheService, WorkspaceCacheService>();

        // 6. Hosted Background Consumer Services
        services.AddHostedService<DocumentSecurityGuardrailConsumerService>();
        services.AddHostedService<DocumentEmbeddingIndexResultConsumerService>();
        services.AddHostedService<MeetingStartedEventConsumer>();

        // 7. Inter-Service gRPC Clients
        services.AddGrpcClient<UserService.UserServiceClient>(o =>
        {
            o.Address = new Uri(configuration["GrpcSettings:AuthServiceUrl"] ?? "http://localhost:50051");
        });
        services.AddScoped<IAuthIdentityClient, AuthIdentityGrpcClient>();

        services.AddGrpcClient<TranslationRoomService.TranslationRoomServiceClient>(o =>
        {
            o.Address = new Uri(configuration["GrpcSettings:TranslationRoomServiceUrl"] ?? "http://localhost:50052");
        });
        services.AddScoped<ITranslationRoomClient, TranslationRoomGrpcClient>();

        return services;
    }
}
