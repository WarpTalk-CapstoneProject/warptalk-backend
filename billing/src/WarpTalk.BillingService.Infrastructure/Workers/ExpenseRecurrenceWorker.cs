using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// Writes the next occurrence of every recurring operating expense (G12) as a planned row a week before
/// it is due, so it shows in the expense list, the budgets and the pending-work inbox, and advances the
/// series. Hourly is plenty for a daily cadence. One replica at a time through the lease; the unique
/// index on (recurring_source_id, expense_date) makes a second writer harmless even without it.
/// </summary>
public sealed class ExpenseRecurrenceWorker : BackgroundService
{
    public const string LockResource = "billing:expense-recurrence";

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly IDistributedLockProvider _locks;
    private readonly TimeProvider _time;
    private readonly ILogger<ExpenseRecurrenceWorker> _logger;

    public ExpenseRecurrenceWorker(
        IServiceProvider services,
        IDistributedLockProvider locks,
        ILogger<ExpenseRecurrenceWorker> logger,
        TimeProvider? time = null)
    {
        _services = services;
        _locks = locks;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _locks.TryRunExclusiveAsync(LockResource, TimeSpan.FromMinutes(5), RunOnceAsync, _logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExpenseRecurrenceWorker: generating recurring expenses failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var expenses = scope.ServiceProvider.GetRequiredService<IOperatingExpenseService>();
        var written = await expenses.GenerateDueOccurrencesAsync(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime), ct);
        if (written > 0)
        {
            _logger.LogInformation("ExpenseRecurrenceWorker wrote {Count} planned occurrence(s) of recurring expenses.", written);
        }
    }
}
