using RailRouteHelper.Core;

namespace RailRouteHelper.Runtime;

public interface IRuntimeSnapshotSource
{
    ValueTask<CapturedRuntimeSnapshot?> CaptureAsync(
        CancellationToken cancellationToken);
}

public sealed record CapturedRuntimeSnapshot(
    string NetworkId,
    OperationalSnapshot Snapshot);
