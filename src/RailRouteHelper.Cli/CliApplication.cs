using System.Text;
using RailRouteHelper.Monitoring;
using RailRouteHelper.Operations;
using RailRouteHelper.Protocol;
using RailRouteHelper.SaveFiles;
using RailRouteHelper.SaveSchema;

namespace RailRouteHelper.Cli;

internal static class CliApplication
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (arguments.Count == 1
            && arguments[0] is "--help" or "-h")
        {
            WriteUsage(output);
            return 0;
        }

        if (arguments.Count == 2
            && arguments[0] == "analyze-save")
        {
            var current = await LoadAsync(
                arguments[1],
                cancellationToken);
            WriteHeader(output, current.Document, current.Mapping);
            WriteReport(
                output,
                new OperationsAnalyzer().Analyze(current.Mapping.Snapshot));
            return 0;
        }

        if (arguments.Count == 3
            && arguments[0] == "compare-saves")
        {
            var previous = await LoadAsync(
                arguments[1],
                cancellationToken);
            var current = await LoadAsync(
                arguments[2],
                cancellationToken);
            WriteHeader(output, current.Document, current.Mapping);
            output.WriteLine($"previousSave: {previous.Document.SourcePath}");
            WriteReport(
                output,
                new OperationsAnalyzer().Analyze(
                    current.Mapping.Snapshot,
                    previous.Mapping.Snapshot));
            return 0;
        }

        if (arguments.Count >= 2
            && arguments[0] == "watch-saves"
            && TryParseWatchArguments(
                arguments,
                out var watchArguments))
        {
            return await WatchSavesAsync(
                watchArguments,
                output,
                cancellationToken);
        }

        error.WriteLine("error: invalid command or argument count.");
        WriteUsage(error);
        return 1;
    }

    private static async Task<LoadedSave> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var document = await new MessagePackLz4SaveFileAdapter().ReadAsync(
            path,
            cancellationToken);
        var mapping = SaveSchemaMapperRegistry.CreateDefault().Map(document);
        return new LoadedSave(document, mapping);
    }

    private static async Task<int> WatchSavesAsync(
        WatchArguments arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetFullPath(arguments.DirectoryPath);
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException(
                $"The save directory does not exist: {directoryPath}");
        }

        await using var recording = arguments.RecordingPath is null
            ? null
            : new FileStream(
                Path.GetFullPath(arguments.RecordingPath),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        var options = new SaveDirectoryWatchOptions
        {
            Follow = arguments.Follow,
        };
        await foreach (var envelope in new SaveDirectoryMonitor().WatchAsync(
                           directoryPath,
                           options,
                           cancellationToken))
        {
            var line = RealtimeProtocolCodec.EncodeLine(envelope);
            await output.WriteAsync(
                Encoding.UTF8.GetString(line).AsMemory(),
                cancellationToken);
            await output.FlushAsync(cancellationToken);
            if (recording is not null)
            {
                await recording.WriteAsync(line, cancellationToken);
                await recording.FlushAsync(cancellationToken);
            }
        }

        return 0;
    }

    private static bool TryParseWatchArguments(
        IReadOnlyList<string> arguments,
        out WatchArguments result)
    {
        var directoryPath = arguments[1];
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            result = default!;
            return false;
        }

        string? recordingPath = null;
        var follow = true;
        var onceSeen = false;
        for (var index = 2; index < arguments.Count; index++)
        {
            if (arguments[index] == "--once" && !onceSeen)
            {
                follow = false;
                onceSeen = true;
                continue;
            }

            if (arguments[index] == "--record"
                && recordingPath is null
                && index + 1 < arguments.Count)
            {
                recordingPath = arguments[++index];
                if (string.IsNullOrWhiteSpace(recordingPath))
                {
                    result = default!;
                    return false;
                }

                if (recordingPath.EndsWith(
                        ".mp.lz4",
                        StringComparison.OrdinalIgnoreCase))
                {
                    result = default!;
                    return false;
                }

                continue;
            }

            result = default!;
            return false;
        }

        result = new WatchArguments(
            directoryPath,
            recordingPath,
            follow);
        return true;
    }

    private static void WriteHeader(
        TextWriter output,
        SaveDocument document,
        SaveMappingResult mapping)
    {
        output.WriteLine($"save: {document.SourcePath}");
        output.WriteLine($"schema: {mapping.SchemaId}");
        output.WriteLine($"gameVersion: {mapping.Snapshot.GameVersion}");
        output.WriteLine(
            $"gameTimeTicks: {mapping.Snapshot.GameTimeTicks?.ToString() ?? "-"}");
        foreach (var diagnostic in mapping.Diagnostics)
        {
            output.WriteLine(
                $"diagnostic: {diagnostic.Severity} "
                + $"{diagnostic.Code} {diagnostic.Message}");
        }
    }

    private static void WriteReport(
        TextWriter output,
        OperationsReport report)
    {
        output.WriteLine($"trains: {report.Trains.Count}");
        foreach (var train in report.Trains)
        {
            output.WriteLine(
                $"train: {train.ReportingNumber} id={train.TrainId}");
            output.WriteLine(
                $"  occupied: {JoinOrDash(train.OccupiedNodeIds)}");
            output.WriteLine(
                $"  currentStation: "
                + $"{train.CurrentLocation?.StationName ?? "-"}");
            output.WriteLine(
                $"  currentPlatform: "
                + $"{train.CurrentLocation?.PlatformNumber?.ToString() ?? "-"}");
            output.WriteLine(
                $"  currentTrack: "
                + $"{train.CurrentLocation?.TrackNodeId ?? "-"}");
            output.WriteLine(
                $"  nextStation: "
                + $"{train.NextDestination?.StationName ?? "-"}");
            output.WriteLine(
                $"  nextPlatform: "
                + $"{train.NextDestination?.PlatformNumber?.ToString() ?? "-"}");
            output.WriteLine(
                $"  nextTrack: "
                + $"{train.NextDestination?.TrackNodeId ?? "-"}");
            output.WriteLine($"  reachability: {train.Reachability}");
            output.WriteLine($"  status: {train.Status}");
            output.WriteLine(
                $"  clearedThrough: {train.ClearedThroughNodeId ?? "-"}");
            output.WriteLine(
                $"  firstUncleared: {train.FirstUnclearedNodeId ?? "-"}");
            foreach (var evidence in train.Evidence)
            {
                output.WriteLine(
                    $"  evidence: {evidence.Certainty} "
                    + $"{evidence.Code} {evidence.Description}");
            }
        }

        output.WriteLine($"routeChanges: {report.RouteChanges.Count}");
        foreach (var change in report.RouteChanges)
        {
            output.WriteLine(
                $"routeChange: {change.Kind} node={change.ControlNodeId}");
            output.WriteLine(
                $"  previousTargets: "
                + $"{JoinOrDash(change.PreviousTargetNodeIds)}");
            output.WriteLine(
                $"  currentTargets: "
                + $"{JoinOrDash(change.CurrentTargetNodeIds)}");
            output.WriteLine(
                $"  previousDestination: "
                + $"{FormatDestination(change.PreviousDestination)}");
            output.WriteLine(
                $"  currentDestination: "
                + $"{FormatDestination(change.CurrentDestination)}");
        }
    }

    private static string FormatDestination(StationTrackLocation? destination) =>
        destination is null
            ? "-"
            : $"{destination.StationName} platform "
                + $"{destination.PlatformNumber?.ToString() ?? "?"} "
                + $"({destination.TrackNodeId})";

    private static string JoinOrDash(IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0
            ? "-"
            : string.Join(", ", materialized);
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Rail Route Helper");
        writer.WriteLine(
            "  railroutehelper analyze-save <save.mp.lz4>");
        writer.WriteLine(
            "  railroutehelper compare-saves <before.mp.lz4> <after.mp.lz4>");
        writer.WriteLine(
            "  railroutehelper watch-saves <directory> "
            + "[--once] [--record <recording.jsonl>]");
    }

    private sealed record LoadedSave(
        SaveDocument Document,
        SaveMappingResult Mapping);

    private sealed record WatchArguments(
        string DirectoryPath,
        string? RecordingPath,
        bool Follow);
}
