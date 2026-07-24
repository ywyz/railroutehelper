using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using RailRouteHelper.LiveOperations;
using RailRouteHelper.Operations;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.Web.Tests;

public sealed class LocalDashboardHttpTests
{
    [Fact]
    public async Task Loopback_dashboard_serves_page_and_live_projection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projector = new LiveOperationsProjector();
        projector.Apply(CreatePossibleBlockedEnvelope());
        var options = new LocalDashboardOptions(
            new Uri("http://127.0.0.1:0"),
            saveDirectory: null);
        await using var application = LocalDashboardApplication.Build(
            options,
            projector);
        await application.StartAsync(cancellationToken);
        var address = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(address),
        };

        var page = await client.GetStringAsync("/", cancellationToken);
        var response = await client.GetAsync(
            "/api/live",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var state = await JsonDocument.ParseAsync(
            content,
            cancellationToken: cancellationToken);

        Assert.Contains("Rail Route Helper", page, StringComparison.Ordinal);
        Assert.Equal(
            42,
            state.RootElement.GetProperty("lastSequence").GetInt64());
        var network = Assert.Single(
            state.RootElement.GetProperty("networks").EnumerateArray());
        Assert.Equal(
            "web-network",
            network.GetProperty("networkId").GetString());
        var alert = Assert.Single(
            state.RootElement.GetProperty("alerts").EnumerateArray());
        Assert.Equal("active", alert.GetProperty("status").GetString());
        Assert.Equal(
            "possibleBlockedTrain",
            alert.GetProperty("kind").GetString());

        await application.StopAsync(cancellationToken);
    }

    private static RealtimeEnvelope CreatePossibleBlockedEnvelope()
    {
        var train = new TrainOperationsAssessment(
            "web-train",
            "W100",
            ["Node:Track:web"],
            null,
            new StationTrackLocation(
                "Web Station",
                "Node:Track:web-platform",
                2),
            TrainRouteReachability.NotReachable,
            TrainOperationalStatus.PossibleBlocked,
            null,
            "Node:Track:web-gap",
            []);
        return OperationsReportProtocol.CreateEnvelope(
            42,
            DateTimeOffset.UnixEpoch.AddHours(2),
            "web.mp.lz4",
            "synthetic/v1",
            "web-network",
            "2.3.24",
            42,
            new OperationsReport([train], []));
    }
}
