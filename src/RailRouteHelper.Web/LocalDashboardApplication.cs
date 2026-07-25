using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using RailRouteHelper.LiveOperations;
using RailRouteHelper.Runtime;

namespace RailRouteHelper.Web;

public static class LocalDashboardApplication
{
    public static WebApplication Build(
        LocalDashboardOptions options,
        LiveOperationsProjector? projector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        projector ??= new LiveOperationsProjector();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(
            options.ListenUri.GetLeftPart(UriPartial.Authority));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(projector);
        RuntimeTelemetryServer? runtimeServer = null;
        if (options.RuntimePort is { } runtimePort)
        {
            runtimeServer = new RuntimeTelemetryServer(runtimePort);
            builder.Services.AddSingleton(runtimeServer);
            builder.Services.AddSingleton<RuntimeOperationsPipeline>();
            builder.Services.AddHostedService<RuntimeProjectionService>();
        }

        builder.Services.Configure<JsonOptions>(
            json =>
            {
                json.SerializerOptions.PropertyNamingPolicy =
                    JsonNamingPolicy.CamelCase;
                json.SerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(
                        JsonNamingPolicy.CamelCase));
            });
        if (options.SaveDirectory is not null)
        {
            builder.Services.AddHostedService<SaveDirectoryProjectionService>();
        }

        var application = builder.Build();
        application.Use(
            async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Append(
                    "Content-Security-Policy",
                    "default-src 'self'; "
                    + "style-src 'self' 'unsafe-inline'; "
                    + "script-src 'self' 'unsafe-inline'; "
                    + "connect-src 'self'; "
                    + "img-src 'self' data:; "
                    + "frame-ancestors 'none'");
                context.Response.Headers.Append(
                    "Referrer-Policy",
                    "no-referrer");
                context.Response.Headers.Append(
                    "X-Content-Type-Options",
                    "nosniff");
                await next(context);
            });
        application.MapGet(
            "/",
            () => Results.Content(
                DashboardPage.Html,
                "text/html; charset=utf-8",
                Encoding.UTF8));
        application.MapGet(
            "/api/live",
            (LiveOperationsProjector liveProjector) =>
                Results.Ok(liveProjector.Current));
        application.MapGet(
            "/api/runtime",
            () => runtimeServer is null
                ? Results.Ok(
                    new
                    {
                        mode = "save-directory",
                        isListening = false,
                    })
                : Results.Ok(runtimeServer.Status));

        return application;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = LocalDashboardOptions.Parse(args);
            await using var application = Build(options);
            Console.WriteLine(
                $"Rail Route Helper dashboard: {options.ListenUri}");
            Console.WriteLine(options.RuntimePort is { } runtimePort
                ? $"Runtime telemetry: tcp://127.0.0.1:{runtimePort}"
                : $"Watching saves: {options.SaveDirectory}");
            await application.RunAsync();
            return 0;
        }
        catch (Exception error) when (
            error is ArgumentException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(
                "Usage: railroutehelper-web "
                + "[--runtime-port 5081] "
                + "[--listen http://127.0.0.1:5080]\n"
                + "   or: railroutehelper-web <save-directory> "
                + "[--listen http://127.0.0.1:5080]");
            return 1;
        }
    }
}
