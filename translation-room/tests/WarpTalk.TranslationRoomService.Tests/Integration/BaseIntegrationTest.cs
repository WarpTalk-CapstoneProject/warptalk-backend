using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

public abstract class BaseIntegrationTest : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    protected HttpClient Client { get; private set; } = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Remove existing DbContext registration
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<TranslationRoomDbContext>));
                    if (descriptor != null) services.Remove(descriptor);

                    // Add test DbContext with Testcontainer connection string
                    services.AddDbContext<TranslationRoomDbContext>(options =>
                        options.UseNpgsql(_dbContainer.GetConnectionString()));

                    // Remove existing Redis registration
                    var redisDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(IConnectionMultiplexer));
                    if (redisDescriptor != null) services.Remove(redisDescriptor);

                    // Add mocked Redis multiplexer
                    var mockRedis = new Mock<IConnectionMultiplexer>();
                    var mockDatabase = new Mock<IDatabase>();
                    var mockSubscriber = new Mock<ISubscriber>();
                    
                    mockDatabase.Setup(d => d.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                        .ReturnsAsync(true);
                    mockDatabase.Setup(d => d.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                        .ReturnsAsync(Array.Empty<StreamEntry>());
                    mockDatabase.Setup(d => d.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                        .ReturnsAsync(1L);
                    mockDatabase.Setup(d => d.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                        .ReturnsAsync(new RedisValue("dummy-id"));
                    mockDatabase.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                        .ReturnsAsync(true);

                    mockSubscriber.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
                        .Returns(Task.CompletedTask);
                    mockSubscriber.Setup(s => s.UnsubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
                        .Returns(Task.CompletedTask);
                    mockSubscriber.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                        .ReturnsAsync(1L);

                    mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);
                    mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);
                    services.AddSingleton<IConnectionMultiplexer>(mockRedis.Object);

                    // Add Test Auth
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", options => { });
                    
                    services.AddAuthorization(options =>
                    {
                        options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Test")
                            .RequireAuthenticatedUser()
                            .Build();
                    });
                });
            });

        try
        {
            Client = _factory.CreateClient();
            var server = _factory.Server;
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("c:\\Users\\Admin\\Documents\\WarpTalk - Capstone Project\\host_startup_error.txt", ex.ToString());
            throw;
        }
        Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        // Run Migrations
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        db.Database.ExecuteSqlRaw("CREATE OR REPLACE FUNCTION public.uuid_generate_v7() RETURNS uuid AS $$ BEGIN RETURN gen_random_uuid(); END; $$ LANGUAGE plpgsql;");
        await db.Database.EnsureCreatedAsync();

        db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS translation_room;");
        db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS platform;");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS platform.supported_languages (code CHAR(5) PRIMARY KEY, name VARCHAR(100) NOT NULL, is_active BOOLEAN DEFAULT TRUE);");
        db.Database.ExecuteSqlRaw("INSERT INTO platform.supported_languages (code, name) VALUES ('en', 'English'), ('vi', 'Vietnamese'), ('fr', 'French'), ('es', 'Spanish') ON CONFLICT DO NOTHING;");
    }

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        _factory.Dispose();
    }
}
