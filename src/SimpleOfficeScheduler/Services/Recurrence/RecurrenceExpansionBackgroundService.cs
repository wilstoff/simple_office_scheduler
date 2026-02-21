using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Recurrence;

public class RecurrenceExpansionBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly RecurrenceSettings _settings;
    private readonly ILogger<RecurrenceExpansionBackgroundService> _logger;

    public RecurrenceExpansionBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<RecurrenceSettings> settings,
        ILogger<RecurrenceExpansionBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpandRecurringEvents(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error expanding recurring events.");
            }

            await Task.Delay(TimeSpan.FromHours(_settings.ExpansionCheckIntervalHours), stoppingToken);
        }
    }

    private async Task ExpandRecurringEvents(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expander = scope.ServiceProvider.GetRequiredService<RecurrenceExpander>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var now = clock.GetCurrentInstant();

        var recurringEvents = await db.Events
            .Include(e => e.Occurrences)
            .Where(e => e.Recurrence != null)
            .ToListAsync(ct);

        int totalAdded = 0;

        foreach (var evt in recurringEvents)
        {
            var zone = TimeZoneHelper.GetZone(evt.TimeZoneId);
            var nowInTz = now.InZone(zone).LocalDateTime;
            var horizon = nowInTz.PlusMonths(_settings.DefaultHorizonMonths);

            var existingTimes = evt.Occurrences.Select(o => o.StartTime).ToHashSet();
            var dates = expander.Expand(evt, horizon);

            foreach (var (start, end) in dates)
            {
                if (!existingTimes.Contains(start))
                {
                    db.EventOccurrences.Add(new EventOccurrence
                    {
                        EventId = evt.Id,
                        StartTime = start,
                        EndTime = end
                    });
                    totalAdded++;
                }
            }
        }

        if (totalAdded > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Expanded {Count} new occurrences for recurring events.", totalAdded);
        }
    }
}
