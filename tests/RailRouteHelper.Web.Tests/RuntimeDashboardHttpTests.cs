using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RailRouteHelper.Core;
using RailRouteHelper.Runtime;

namespace RailRouteHelper.Web.Tests;

public sealed class RuntimeDashboardHttpTests
{
    [Fact]
    public async Task Runtime_tcp_snapshot_reaches_live_http_api()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = new LocalDashboardOptions(
            new Uri("http://127.0.0.1:0"),
            saveDirectory: null,
            runtimePort: 0);
        await using var application = LocalDashboardApplication.Build(options);
        await application.StartAsync(cancellationToken);
        var server = application.Services
            .GetRequiredService<RuntimeTelemetryServer>();
        await WaitUntilAsync(
            () => server.Status.IsListening,
            cancellationToken);

        await using (var runtimeClient = new RuntimeSnapshotClient(server.Port))
        {
            await runtimeClient.ConnectAsync(cancellationToken);
            await runtimeClient.PublishAsync(
                0,
                DateTimeOffset.UnixEpoch.AddMinutes(5),
                "web-runtime-session",
                "web-runtime-network",
                EmptySnapshot(),
                cancellationToken);
        }

        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(application.Urls.Single()),
        };
        await WaitUntilAsync(
            async () =>
            {
                var state = await client.GetFromJsonAsync<LiveState>(
                    "/api/live",
                    cancellationToken);
                return state?.LastSequence == 0;
            },
            cancellationToken);
        var status = await client.GetFromJsonAsync<RuntimeStatus>(
            "/api/runtime",
            cancellationToken);

        Assert.NotNull(status);
        Assert.True(status.IsListening);
        Assert.Equal(1, status.AcceptedFrames);
        await application.StopAsync(cancellationToken);
    }

    private static OperationalSnapshot EmptySnapshot() => new(
        new GameVersion(3, 0, 0),
        DateTimeOffset.UnixEpoch.AddMinutes(5),
        300,
        [],
        [],
        [],
        []);

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        await WaitUntilAsync(
            () => Task.FromResult(condition()),
            cancellationToken);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException("The expected Web runtime state was not reached.");
    }

    private sealed record LiveState(long? LastSequence);

    private sealed record RuntimeStatus(bool IsListening, long AcceptedFrames);
}
