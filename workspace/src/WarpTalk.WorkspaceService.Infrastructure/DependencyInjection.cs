using System;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;
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
using WarpTalk.WorkspaceService.Infrastructure.Outbox;

namespace WarpTalk.WorkspaceService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // 1. Database Context
        var connectionString = configuration.GetConnectionString("WorkspaceDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:WorkspaceDb is required outside Development.");
            }

            connectionString =
                "Host=localhost;Database=warptalk;Username=postgres;Password=postgres;Search Path=workspace,public";
        }
        services.AddDbContext<WorkspaceDbContext>(options =>
            options.UseNpgsql(connectionString));

        // 2. Repositories & Unit of Work
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IWorkspaceMemberRepository, WorkspaceMemberRepository>();
        services.AddScoped<IWorkspaceInvitationRepository, WorkspaceInvitationRepository>();
        services.AddScoped<IAdminAuditLogRepository, AdminAuditLogRepository>();

        // 3. Object Storage Options & Adapters
        services.AddWarpTalkObjectStorageOptions(configuration);

        var storageOptions = configuration.GetSection(ObjectStorageOptions.SectionName).Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
        ValidateObjectStorageConfiguration(storageOptions, environment);
        if (!storageOptions.UsesS3CompatibleProvider && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Workspace document storage must use an S3-compatible provider outside Development.");
        }
        if (storageOptions.UsesS3CompatibleProvider)
        {
            services.AddSingleton<IAmazonS3>(sp =>
            {
                var serviceUrl = storageOptions.S3.ServiceUrl
                                 ?? throw new InvalidOperationException("Storage:S3:ServiceUrl is required for S3-compatible storage.");
                var s3Config = new AmazonS3Config
                {
                    ServiceURL = serviceUrl,
                    ForcePathStyle = true,
                    UseHttp = !serviceUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase)
                };
                var accessKey = storageOptions.S3.AccessKey
                                ?? throw new InvalidOperationException("Storage:S3:AccessKey is required for S3-compatible storage.");
                var secretKey = storageOptions.S3.SecretKey
                                ?? throw new InvalidOperationException("Storage:S3:SecretKey is required for S3-compatible storage.");
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

        // Reading indexed chunks back out. Indexing is fire-and-forget over Redis; this is a
        // synchronous read a user is waiting on, so it goes to the store directly. A typed
        // client keeps the base address and timeout in one place and out of the adapter.
        var vectorDbUrl = configuration["VectorDb:Url"];
        if (string.IsNullOrWhiteSpace(vectorDbUrl))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "VectorDb:Url is required outside Development.");
            }

            vectorDbUrl = "http://localhost:6333";
        }
        services.AddHttpClient<IKnowledgeChunkReader, QdrantKnowledgeChunkReader>(client =>
        {
            // A trailing slash keeps the relative request path from replacing the last
            // segment of the base address.
            client.BaseAddress = new Uri(vectorDbUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);

            var vectorDbApiKey = configuration["VectorDb:ApiKey"];
            if (!string.IsNullOrWhiteSpace(vectorDbApiKey))
            {
                client.DefaultRequestHeaders.Add("api-key", vectorDbApiKey);
            }
        });
        services.AddScoped<IDocumentEmbeddingResultProcessor, DocumentEmbeddingResultProcessor>();
        services.AddScoped<WorkspaceOutboxWriter>();
        services.AddScoped<WorkspaceOutboxDelivery>();
        services.AddScoped<WorkspaceOutboxDispatcher>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<WorkspaceDocumentAuxiliaryPublisher>();
        services.AddScoped<IWorkspaceDocumentEventPublisher, OutboxWorkspaceDocumentEventPublisher>();
        services.AddScoped<IWorkspaceEventPublisher, OutboxWorkspaceEventPublisher>();

        // 5. Distributed Redis Cache & Multiplexer
        var redisConnectionString = configuration["Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Redis:ConnectionString is required outside Development.");
            }

            redisConnectionString = "localhost:6379";
        }
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
        services.AddHostedService<WorkspaceOutboxWorker>();
        // WT-263: replicates BillingService's resolved entitlements into the local snapshot table
        // that meeting-creation enforcement reads. Guarded — see the class comment.
        services.AddHostedService<EntitlementsChangedConsumer>();

        // 7. Inter-Service gRPC Clients
        services.AddGrpcClient<UserService.UserServiceClient>(o =>
        {
            o.Address = configuration.GetRequiredServiceUri(
                environment,
                "GrpcSettings:AuthServiceUrl",
                "http://localhost:50051");
        })
        .AddWarpTalkGrpcClientDefaults(configuration, environment);
        services.AddScoped<IAuthIdentityClient, AuthIdentityGrpcClient>();

        services.AddGrpcClient<TranslationRoomService.TranslationRoomServiceClient>(o =>
        {
            o.Address = configuration.GetRequiredServiceUri(
                environment,
                "GrpcSettings:TranslationRoomServiceUrl",
                "http://localhost:50052");
        })
        .AddWarpTalkGrpcClientDefaults(configuration, environment);
        services.AddScoped<ITranslationRoomClient, TranslationRoomGrpcClient>();

        services.AddGrpcClient<BillingService.BillingServiceClient>(o =>
        {
            o.Address = configuration.GetRequiredServiceUri(
                environment,
                "GrpcSettings:BillingServiceUrl",
                "http://localhost:50057");
        })
        .AddWarpTalkGrpcClientDefaults(configuration, environment);
        services.AddScoped<IBillingSubscriptionClient, BillingSubscriptionGrpcClient>();

        return services;
    }

    private static void ValidateObjectStorageConfiguration(
        ObjectStorageOptions options,
        IHostEnvironment environment)
    {
        var masterKeyInvalid =
            string.IsNullOrWhiteSpace(options.MasterKey)
            || options.MasterKey.Length < 32
            || options.MasterKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || options.MasterKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
        if (masterKeyInvalid && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY ERROR: Storage:MasterKey must contain at least 32 characters and must not be a placeholder.");
        }
        if (string.IsNullOrWhiteSpace(options.MasterKey))
        {
            throw new InvalidOperationException(
                "Storage:MasterKey is required. Development may use an explicit local-only value, but no implicit fallback is provided.");
        }

        if (!options.UsesS3CompatibleProvider)
        {
            return;
        }

        var s3CredentialsInvalid =
            string.IsNullOrWhiteSpace(options.S3.AccessKey)
            || string.IsNullOrWhiteSpace(options.S3.SecretKey)
            || options.S3.AccessKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || options.S3.SecretKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || options.S3.AccessKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            || options.S3.SecretKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
        if (s3CredentialsInvalid && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY ERROR: non-placeholder Storage:S3 credentials are required outside Development.");
        }
    }
}
