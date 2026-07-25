namespace RailRouteHelper.Runtime;

public sealed record RuntimeTelemetryStatus(
    bool IsListening,
    int Port,
    bool IsCollectorConnected,
    long AcceptedConnections,
    long AcceptedFrames,
    long RejectedConnections,
    DateTimeOffset? LastFrameAtUtc,
    string? LastError)
{
    public static RuntimeTelemetryStatus Stopped(int port) => new(
        false,
        port,
        false,
        0,
        0,
        0,
        null,
        null);
}
