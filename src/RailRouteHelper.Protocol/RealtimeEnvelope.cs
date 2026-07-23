using System.Text.Json;

namespace RailRouteHelper.Protocol;

public sealed record RealtimeEnvelope(
    int ProtocolVersion,
    long Sequence,
    DateTimeOffset CapturedAtUtc,
    string MessageType,
    JsonElement Payload);

