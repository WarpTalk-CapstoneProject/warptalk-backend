using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Application.Services;

namespace WarpTalk.TranscriptService.Infrastructure.Redis;

/// <summary>
/// Reads warptalk-ai's <c>embedding:index_results</c> for <c>glossary_term</c> jobs and settles
/// the per-glossary "loaded into WarpBot" counters (<see cref="GlossaryWarpBotStatus"/>).
///
/// Its own consumer group, so WorkspaceService's document group on the same shared stream is
/// unaffected. Every message is acknowledged — a result for another producer is not ours, and a
/// result this service cannot count must not wedge the group's pending list (the base class
/// re-reads pending messages until they are acknowledged).
/// </summary>
public sealed class GlossaryIndexResultConsumer : BaseRedisConsumer
{
    public GlossaryIndexResultConsumer(IConnectionMultiplexer redis, ILogger<GlossaryIndexResultConsumer> logger)
        : base(redis, logger)
    {
    }

    protected override string StreamKey => "embedding:index_results";
    protected override string ConsumerGroup => "transcript-glossary-index";
    protected override string ConsumerName => $"transcript-{Environment.MachineName}";

    protected override async Task<bool> ProcessMessageAsync(StreamEntry message, CancellationToken stoppingToken)
    {
        try
        {
            string? Field(string name) => message.Values.FirstOrDefault(v => v.Name == name).Value.ToString();

            if (!string.Equals(Field("source_type"), "glossary_term", StringComparison.OrdinalIgnoreCase))
                return true;

            var jobId = Field("job_id");
            var status = Field("status");
            if (string.IsNullOrEmpty(jobId) || string.IsNullOrEmpty(status))
                return true;

            await GlossaryWarpBotStatus.RecordResultAsync(_redis.GetDatabase(), jobId, status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count glossary index result {MessageId}", message.Id);
        }
        return true;
    }
}
