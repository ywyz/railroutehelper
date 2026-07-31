using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExportLulutongTrainRoutes;

/// <summary>
/// 将用户本机的路路通 APK 中的车次索引和路线资源转换为桌面端可读的 JSON。
/// 不会把 APK 或导出的时刻数据写入仓库。
/// </summary>
internal static class Program
{
    private const string TrainCodeIndexEntryName = "res/DO";       // t_i
    private const string TrainNoIndexEntryName = "res/k5.dat";     // sp
    private const string RoutesEntryName = "res/hU.dat";           // routes
    private const int MaxRecordCount = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (options is null)
            {
                PrintUsage();
                return 2;
            }

            if (!File.Exists(options.ApkPath))
            {
                Console.Error.WriteLine($"找不到 APK：{options.ApkPath}");
                return 2;
            }

            var result = Export(options.ApkPath);
            if (result.Routes.Count == 0)
            {
                Console.Error.WriteLine("未能从 APK 导出任何可安全匹配的车次路线。");
                return 1;
            }

            var generatedAtUtc = DateTimeOffset.UtcNow;
            var apkSha256 = ComputeSha256(options.ApkPath);
            var dataFile = new OfflineRoutesFile
            {
                SchemaVersion = 1,
                Source = "lulutong-apk-local-export",
                GeneratedAtUtc = generatedAtUtc,
                ApkSha256 = apkSha256,
                Routes = result.Routes
            };
            var report = new ExportReport
            {
                ApkFileName = Path.GetFileName(options.ApkPath),
                ApkSha256 = apkSha256,
                SourceEntries = new[]
                {
                    TrainCodeIndexEntryName,
                    TrainNoIndexEntryName,
                    RoutesEntryName
                },
                GeneratedAtUtc = generatedAtUtc,
                TrainCodeIndexEntries = result.TrainCodeIndexEntries,
                TrainNoIndexEntries = result.TrainNoIndexEntries,
                RouteRecordsRead = result.RouteRecordsRead,
                ExportedCodes = result.Routes.Count,
                UnmappedCodes = result.UnmappedCodes,
                ConflictingCodes = result.ConflictingCodes
            };

            WriteJsonAtomically(options.OutputPath, dataFile);
            WriteJsonAtomically(options.ReportPath, report);

            Console.WriteLine($"已导出 {dataFile.Routes.Count:N0} 条车次始发终到：{options.OutputPath}");
            Console.WriteLine($"未映射 {report.UnmappedCodes.Count:N0} 条；冲突 {report.ConflictingCodes.Count:N0} 条。");
            Console.WriteLine($"审计报告：{options.ReportPath}");
            return 0;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"APK 离线数据格式无效：{exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"导出失败：{exception.Message}");
            return 1;
        }
    }

    private static ExportResult Export(string apkPath)
    {
        using var archive = ZipFile.OpenRead(apkPath);
        var trainCodes = ReadJavaUtfIndex(ReadRequiredEntry(archive, TrainCodeIndexEntryName));
        var trainNos = ReadTrainNoIndex(ReadRequiredEntry(archive, TrainNoIndexEntryName));
        if (trainCodes.Count != trainNos.Count)
        {
            throw new InvalidDataException(
                $"车号索引条数 {trainCodes.Count:N0} 与内部编号索引条数 {trainNos.Count:N0} 不一致。");
        }

        var routesByTrainNo = ReadRoutes(ReadRequiredEntry(archive, RoutesEntryName));
        var candidatesByCode = new Dictionary<string, List<RouteCandidate>>(StringComparer.OrdinalIgnoreCase);
        var unmappedCodes = new List<UnmappedCode>();

        for (var index = 0; index < trainCodes.Count; index++)
        {
            var code = NormalizeCode(trainCodes[index]);
            var trainNo = trainNos[index];
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(trainNo))
            {
                unmappedCodes.Add(new UnmappedCode(index, trainCodes[index], trainNo, "空车号或内部编号"));
                continue;
            }

            if (!routesByTrainNo.TryGetValue(trainNo, out var route))
            {
                unmappedCodes.Add(new UnmappedCode(index, code, trainNo, "路线表未包含该内部编号"));
                continue;
            }

            foreach (var codeAlias in ExpandCodeAliases(code))
            {
                if (!candidatesByCode.TryGetValue(codeAlias, out var candidates))
                {
                    candidates = new List<RouteCandidate>();
                    candidatesByCode.Add(codeAlias, candidates);
                }

                candidates.Add(new RouteCandidate(trainNo, route.Origin, route.Destination));
            }
        }

        var routes = new Dictionary<string, OfflineRoute>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new Dictionary<string, List<RouteCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in candidatesByCode)
        {
            var candidate = pair.Value[0];
            var hasDifferentRoute = pair.Value.Any(other =>
                !string.Equals(other.Origin, candidate.Origin, StringComparison.Ordinal) ||
                !string.Equals(other.Destination, candidate.Destination, StringComparison.Ordinal));
            if (hasDifferentRoute)
            {
                conflicts.Add(pair.Key, pair.Value);
                continue;
            }

            routes.Add(pair.Key, new OfflineRoute
            {
                Origin = candidate.Origin,
                Destination = candidate.Destination
            });
        }

        return new ExportResult(
            routes,
            trainCodes.Count,
            trainNos.Count,
            routesByTrainNo.Count,
            unmappedCodes,
            conflicts);
    }

    private static List<string> ReadJavaUtfIndex(ZipArchiveEntry entry)
    {
        var reader = new BigEndianReader(ReadEntryBytes(entry), entry.FullName);
        var count = reader.ReadUInt16();
        ValidateCount(count, "车号索引");
        var values = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            // IndexMgr 使用 DataInputStream.readUTF()；本 APK 的字符串均为普通 UTF-8 兼容内容。
            values.Add(reader.ReadJavaUtf());
        }

        reader.RequireEnd();
        return values;
    }

    private static List<string> ReadTrainNoIndex(ZipArchiveEntry entry)
    {
        var reader = new BigEndianReader(ReadEntryBytes(entry), entry.FullName);
        var tsddCount = reader.ReadUInt32();
        ValidateCount(tsddCount, "sp.tsdd");
        reader.Skip(checked((int)tsddCount * 16));

        var count = reader.ReadUInt32();
        ValidateCount(count, "sp.trainNo");
        var values = new List<string>((int)count);
        for (var index = 0U; index < count; index++)
        {
            values.Add(reader.ReadString8());
        }

        return values;
    }

    private static Dictionary<string, TrainRoute> ReadRoutes(ZipArchiveEntry entry)
    {
        var reader = new BigEndianReader(ReadEntryBytes(entry), entry.FullName);
        var railwayLineCount = reader.ReadUInt32();
        ValidateCount(railwayLineCount, "铁路线路");
        for (var lineIndex = 0U; lineIndex < railwayLineCount; lineIndex++)
        {
            _ = reader.ReadUInt32();
            _ = reader.ReadString8();
            var stationCount = reader.ReadUInt32();
            ValidateCount(stationCount, "线路车站");
            for (var stationIndex = 0U; stationIndex < stationCount; stationIndex++)
            {
                _ = reader.ReadString8();
                _ = reader.ReadString8();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
            }
        }

        var routeRecordCount = reader.ReadUInt32();
        ValidateCount(routeRecordCount, "车次路线");
        var routes = new Dictionary<string, TrainRoute>(StringComparer.Ordinal);
        for (var routeIndex = 0U; routeIndex < routeRecordCount; routeIndex++)
        {
            var trainNo = reader.ReadString8();
            var destination = reader.ReadString8();
            var nodeCount = reader.ReadUInt32();
            ValidateCount(nodeCount, "路线节点");
            string? origin = null;
            for (var nodeIndex = 0U; nodeIndex < nodeCount; nodeIndex++)
            {
                var station = reader.ReadString8();
                if (nodeIndex == 0) origin = station;
                _ = reader.ReadUInt32();
            }

            if (string.IsNullOrWhiteSpace(trainNo) || string.IsNullOrWhiteSpace(origin) ||
                string.IsNullOrWhiteSpace(destination))
            {
                continue;
            }

            if (!routes.TryAdd(trainNo, new TrainRoute(origin, destination)))
            {
                throw new InvalidDataException($"路线表含重复内部编号：{trainNo}");
            }
        }

        reader.RequireEnd();
        return routes;
    }

    private static ZipArchiveEntry ReadRequiredEntry(ZipArchive archive, string entryName)
    {
        return archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"APK 中缺少资源：{entryName}");
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void ValidateCount(uint count, string fieldName)
    {
        if (count > MaxRecordCount)
        {
            throw new InvalidDataException($"{fieldName}数量异常：{count:N0}");
        }
    }

    private static string NormalizeCode(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private static IEnumerable<string> ExpandCodeAliases(string code)
    {
        yield return code;

        foreach (var part in code.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.Equals(part, code, StringComparison.Ordinal))
            {
                yield return part;
            }
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"输出路径必须包含目录：{path}");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".new";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(temporaryPath, json, Utf8);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    private static CommandLineOptions? ParseArguments(string[] args)
    {
        string? apkPath = null;
        string? outputPath = null;
        string? reportPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--help" or "-h") return null;
            if (index == args.Length - 1) return null;

            switch (args[index])
            {
                case "--apk":
                    apkPath = args[++index];
                    break;
                case "--output":
                    outputPath = args[++index];
                    break;
                case "--report":
                    reportPath = args[++index];
                    break;
                default:
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(apkPath)) return null;
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RailRouteAssistant");
        outputPath ??= Path.Combine(dataDirectory, "train_routes_offline.json");
        reportPath ??= Path.Combine(dataDirectory, "train_routes_offline_report.json");
        return new CommandLineOptions(apkPath, outputPath, reportPath);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：dotnet run --project tools/ExportLulutongTrainRoutes -- --apk <lulutong.apk> [--output <json>] [--report <json>]");
    }

    private sealed record CommandLineOptions(string ApkPath, string OutputPath, string ReportPath);

    private sealed record ExportResult(
        Dictionary<string, OfflineRoute> Routes,
        int TrainCodeIndexEntries,
        int TrainNoIndexEntries,
        int RouteRecordsRead,
        List<UnmappedCode> UnmappedCodes,
        Dictionary<string, List<RouteCandidate>> ConflictingCodes);

    private sealed record TrainRoute(string Origin, string Destination);
    private sealed record RouteCandidate(string TrainNo, string Origin, string Destination);
    private sealed record UnmappedCode(int Index, string Code, string TrainNo, string Reason);

    private sealed class OfflineRoutesFile
    {
        public int SchemaVersion { get; init; }
        public string Source { get; init; } = string.Empty;
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public string ApkSha256 { get; init; } = string.Empty;
        public Dictionary<string, OfflineRoute> Routes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class OfflineRoute
    {
        public string Origin { get; init; } = string.Empty;
        public string Destination { get; init; } = string.Empty;
    }

    private sealed class ExportReport
    {
        public string ApkFileName { get; init; } = string.Empty;
        public string ApkSha256 { get; init; } = string.Empty;
        public string[] SourceEntries { get; init; } = Array.Empty<string>();
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public int TrainCodeIndexEntries { get; init; }
        public int TrainNoIndexEntries { get; init; }
        public int RouteRecordsRead { get; init; }
        public int ExportedCodes { get; init; }
        public List<UnmappedCode> UnmappedCodes { get; init; } = new();
        public Dictionary<string, List<RouteCandidate>> ConflictingCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BigEndianReader
    {
        private readonly byte[] _data;
        private readonly string _sourceName;
        private int _offset;

        public BigEndianReader(byte[] data, string sourceName)
        {
            _data = data;
            _sourceName = sourceName;
        }

        public uint ReadUInt32()
        {
            RequireBytes(4);
            var value = ((uint)_data[_offset] << 24) |
                        ((uint)_data[_offset + 1] << 16) |
                        ((uint)_data[_offset + 2] << 8) |
                        _data[_offset + 3];
            _offset += 4;
            return value;
        }

        public ushort ReadUInt16()
        {
            RequireBytes(2);
            var value = (ushort)((_data[_offset] << 8) | _data[_offset + 1]);
            _offset += 2;
            return value;
        }

        public string ReadString8()
        {
            RequireBytes(1);
            var byteLength = _data[_offset++];
            return ReadUtf8Bytes(byteLength);
        }

        public string ReadJavaUtf()
        {
            var byteLength = ReadUInt16();
            return ReadUtf8Bytes(byteLength);
        }

        public void Skip(int byteCount)
        {
            RequireBytes(byteCount);
            _offset += byteCount;
        }

        public void RequireEnd()
        {
            if (_offset != _data.Length)
            {
                throw new InvalidDataException(
                    $"{_sourceName} 解析后仍剩余 {_data.Length - _offset:N0} 字节（偏移 {_offset:N0}）。");
            }
        }

        private string ReadUtf8Bytes(int byteLength)
        {
            RequireBytes(byteLength);
            var value = Utf8.GetString(_data, _offset, byteLength);
            _offset += byteLength;
            return value;
        }

        private void RequireBytes(int count)
        {
            if (count < 0 || _offset > _data.Length - count)
            {
                throw new InvalidDataException($"{_sourceName} 在偏移 {_offset:N0} 意外结束。");
            }
        }
    }
}
