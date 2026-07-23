using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using RailRouteHelper.Core;
using RailRouteHelper.Operations;
using RailRouteHelper.Protocol;
using RailRouteHelper.SaveFiles;
using RailRouteHelper.SaveSchema;

namespace RailRouteHelper.Monitoring;

public sealed class SaveDirectoryMonitor
{
    public async IAsyncEnumerable<RealtimeEnvelope> WatchAsync(
        string directoryPath,
        SaveDirectoryWatchOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        options ??= new SaveDirectoryWatchOptions();
        ValidateOptions(options);

        var directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The save directory does not exist: {directory}");
        }

        var processedRevisions = new HashSet<FileRevision>();
        var previousByNetwork = new Dictionary<string, OperationalSnapshot>(
            StringComparer.Ordinal);
        var latestGameTimeByNetwork = new Dictionary<string, ulong>(
            StringComparer.Ordinal);
        var sequence = options.StartingSequence;
        var analyzer = new OperationsAnalyzer();
        var changes = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
        if (!options.IncludeExisting)
        {
            CaptureCurrentRevisions(directory, processedRevisions);
        }

        using var watcher = options.Follow
            ? CreateWatcher(directory, changes.Writer)
            : null;

        do
        {
            var batch = await LoadCandidatesAsync(
                directory,
                options.FileStabilityInterval,
                processedRevisions,
                cancellationToken);
            foreach (var diagnostic in batch.Diagnostics
                         .OrderBy(
                             item => item.SourceSaveName,
                             StringComparer.Ordinal))
            {
                yield return SaveMonitorDiagnosticProtocol.CreateEnvelope(
                    sequence++,
                    diagnostic.ObservedAtUtc,
                    diagnostic.SourceSaveName,
                    diagnostic.Code,
                    diagnostic.Description);
            }

            foreach (var candidate in batch.Candidates
                         .OrderBy(item => item.NetworkId, StringComparer.Ordinal)
                         .ThenBy(
                             item => item.Mapping.Snapshot.GameTimeTicks
                                 ?? ulong.MaxValue)
                         .ThenBy(item => item.Mapping.Snapshot.ObservedAtUtc)
                         .ThenBy(
                             item => item.Document.SourcePath,
                             StringComparer.Ordinal))
            {
                var snapshot = candidate.Mapping.Snapshot;
                if (snapshot.GameTimeTicks is { } gameTime
                    && latestGameTimeByNetwork.TryGetValue(
                        candidate.NetworkId,
                        out var latestGameTime)
                    && gameTime < latestGameTime)
                {
                    continue;
                }

                previousByNetwork.TryGetValue(
                    candidate.NetworkId,
                    out var previous);
                var report = analyzer.Analyze(snapshot, previous);
                yield return OperationsReportProtocol.CreateEnvelope(
                    sequence++,
                    snapshot.ObservedAtUtc,
                    Path.GetFileName(candidate.Document.SourcePath),
                    candidate.Mapping.SchemaId,
                    candidate.NetworkId,
                    snapshot.GameVersion.ToString(),
                    snapshot.GameTimeTicks,
                    report);
                previousByNetwork[candidate.NetworkId] = snapshot;
                if (snapshot.GameTimeTicks is { } currentGameTime)
                {
                    latestGameTimeByNetwork[candidate.NetworkId] =
                        currentGameTime;
                }
            }

            if (options.Follow)
            {
                await WaitForChangeAsync(
                    changes.Reader,
                    options.ScanInterval,
                    cancellationToken);
            }
        }
        while (options.Follow);
    }

    private static async Task<ScanBatch> LoadCandidatesAsync(
        string directory,
        TimeSpan stabilityInterval,
        ISet<FileRevision> processedRevisions,
        CancellationToken cancellationToken)
    {
        var adapter = new MessagePackLz4SaveFileAdapter();
        var mapper = SaveSchemaMapperRegistry.CreateDefault();
        var candidates = new List<LoadedCandidate>();
        var diagnostics = new List<LoadDiagnostic>();
        foreach (var path in Directory
                     .EnumerateFiles(
                         directory,
                         "*.mp.lz4",
                         SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            var stamp = await ReadStableStampAsync(
                    path,
                    stabilityInterval,
                    cancellationToken);
            if (stamp is null)
            {
                continue;
            }

            var revision = new FileRevision(Path.GetFullPath(path), stamp);
            if (processedRevisions.Contains(revision))
            {
                continue;
            }

            try
            {
                var document = await adapter.ReadAsync(path, cancellationToken);
                var mapping = mapper.Map(document);
                candidates.Add(
                    new LoadedCandidate(
                        document,
                        mapping,
                        ComputeNetworkId(mapping.Snapshot)));
            }
            catch (Exception error) when (
                error is InvalidDataException
                    or InvalidSaveSchemaException
                    or UnsupportedGameVersionException
                    or IOException
                    or UnauthorizedAccessException)
            {
                diagnostics.Add(CreateDiagnostic(path, stamp, error));
            }

            processedRevisions.Add(revision);
        }

        return new ScanBatch(candidates, diagnostics);
    }

    private static LoadDiagnostic CreateDiagnostic(
        string path,
        FileStamp stamp,
        Exception error)
    {
        var (code, description) = error switch
        {
            InvalidDataException => (
                "save-container-invalid",
                "The file is not a valid MessagePack/LZ4 save container."),
            UnsupportedGameVersionException => (
                "save-version-unsupported",
                "The save embeds a game version not supported by this build."),
            InvalidSaveSchemaException => (
                "save-schema-invalid",
                "The save does not match its registered field schema."),
            UnauthorizedAccessException => (
                "save-access-denied",
                "The save could not be read because access was denied."),
            _ => (
                "save-read-failed",
                "The save could not be read."),
        };
        return new LoadDiagnostic(
            Path.GetFileName(path),
            new DateTimeOffset(stamp.LastWriteTimeUtc, TimeSpan.Zero),
            code,
            description);
    }

    private static async Task<FileStamp?> ReadStableStampAsync(
        string path,
        TimeSpan stabilityInterval,
        CancellationToken cancellationToken)
    {
        var first = ReadStamp(path);
        if (first is null)
        {
            return null;
        }

        if (stabilityInterval > TimeSpan.Zero)
        {
            await Task.Delay(stabilityInterval, cancellationToken);
        }

        var second = ReadStamp(path);
        return first == second
            ? second
            : null;
    }

    private static FileSystemWatcher CreateWatcher(
        string directory,
        ChannelWriter<bool> changes)
    {
        var watcher = new FileSystemWatcher(directory, "*.mp.lz4")
        {
            IncludeSubdirectories = false,
            NotifyFilter =
                NotifyFilters.CreationTime
                | NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => changes.TryWrite(true);
        watcher.Created += (_, _) => changes.TryWrite(true);
        watcher.Renamed += (_, _) => changes.TryWrite(true);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static async Task WaitForChangeAsync(
        ChannelReader<bool> changes,
        TimeSpan scanInterval,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(scanInterval);
        try
        {
            await changes.ReadAsync(timeout.Token);
            while (changes.TryRead(out _))
            {
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void CaptureCurrentRevisions(
        string directory,
        ISet<FileRevision> revisions)
    {
        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     "*.mp.lz4",
                     SearchOption.TopDirectoryOnly))
        {
            if (ReadStamp(path) is { } stamp)
            {
                revisions.Add(new FileRevision(Path.GetFullPath(path), stamp));
            }
        }
    }

    private static FileStamp? ReadStamp(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists
            ? new FileStamp(file.Length, file.LastWriteTimeUtc)
            : null;
    }

    private static string ComputeNetworkId(OperationalSnapshot snapshot)
    {
        var identity = new StringBuilder();
        foreach (var track in snapshot.TrackSegments
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            identity.Append("track\0").Append(track.Id).Append('\0');
            foreach (var endpoint in track.EndpointNodeIds
                         .Order(StringComparer.Ordinal))
            {
                identity.Append(endpoint).Append('\0');
            }
        }

        foreach (var platformTrack in snapshot.Stations
                     .SelectMany(station => station.Platforms)
                     .Select(platform => platform.TrackNodeId)
                     .Order(StringComparer.Ordinal))
        {
            identity.Append("platform\0").Append(platformTrack).Append('\0');
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))
            .ToLowerInvariant();
    }

    private static void ValidateOptions(SaveDirectoryWatchOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.StartingSequence);
        if (options.ScanInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The scan interval must be positive.");
        }

        if (options.FileStabilityInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The file stability interval cannot be negative.");
        }
    }

    private sealed record LoadedCandidate(
        SaveDocument Document,
        SaveMappingResult Mapping,
        string NetworkId);

    private sealed record ScanBatch(
        IReadOnlyList<LoadedCandidate> Candidates,
        IReadOnlyList<LoadDiagnostic> Diagnostics);

    private sealed record LoadDiagnostic(
        string SourceSaveName,
        DateTimeOffset ObservedAtUtc,
        string Code,
        string Description);

    private sealed record FileStamp(long Length, DateTime LastWriteTimeUtc);

    private sealed record FileRevision(string Path, FileStamp Stamp);
}
