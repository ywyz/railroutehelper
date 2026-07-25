using RailRouteHelper.Runtime;

namespace RailRouteHelper.Web;

internal sealed class RuntimeProjectionService(
    RuntimeTelemetryServer server,
    RuntimeOperationsPipeline pipeline) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return server.RunAsync(
            (message, _) =>
            {
                pipeline.Apply(message);
                return ValueTask.CompletedTask;
            },
            stoppingToken);
    }
}
