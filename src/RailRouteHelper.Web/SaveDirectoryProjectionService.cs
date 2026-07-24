using RailRouteHelper.LiveOperations;
using RailRouteHelper.Monitoring;

namespace RailRouteHelper.Web;

internal sealed class SaveDirectoryProjectionService(
    LocalDashboardOptions options,
    LiveOperationsProjector projector) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await foreach (var envelope in new SaveDirectoryMonitor().WatchAsync(
                           options.SaveDirectory!,
                           cancellationToken: stoppingToken))
        {
            projector.Apply(envelope);
        }
    }
}
